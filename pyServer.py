"""
This script handles the WebSocket communication between the client and the server.
"""

import asyncio
import cv2
import json
import websockets
import os
import socket
import struct
import threading
import numpy as np
from collections import defaultdict
from aprilTagWrapper import (
    getCameraParameters,
    getAprilTagDetector,
    getTagPoseToWorldFromImage,
    TAG_SIZE,
    F_X,
    F_Y,
    C_X,
    C_Y,
)
from objectDetection import detect_img
from openAIWrapper import (
    update_supported_prefab_types_with_sizes,
    request_transcription_from_openai,
    request_tts_from_openai,
    request_initial_chat,
    request_refined_chat,
    ParsedUserRequest,
    CoordinateSpaceEnum,
    RequestCategoryEnum,
)
from scipy.spatial.transform import Rotation as R
from typing import Dict, Optional
from utils import (
    assign_object_id,
    resize_and_save_image,
    crop_and_save_image,
    transform_object_json,
    transform_object_info,
    clear_user_messages,
)
from whisperWrapper import request_transcription_local

connected_clients = set()
connected_clients_low_level = set()
# Maintain per-user states
user_states = defaultdict(
    lambda: {
        "id": None,
        "image_event": asyncio.Event(),
        "confirm_crop_image": asyncio.Event(),
        "context_data_event": asyncio.Event(),
        "whiteboard_img_event": asyncio.Event(),
        "user_consent": False,
        "context_data": "",
        "ori_img_filename": "",
        "ori_img_width": 0,
        "ori_img_height": 0,
        "resized_img_width": 0,
        "resized_img_height": 0,
        "registry_pose": None,
        "camera_params": None,
        "detected_objects": "",
        "owned_objects": set(),
    }
)
sync_objects = {}
dummy_audio_transcription = {
    "ServerConnected": "You have been connected to the edge server.",
    "FailedRegistration": "User registration failed. Please try again.",
    "ImgRejected": "You have declined the image. I will not proceed with uploading it to the server. Please let me know your request again.",
}
# Create a global lock
audio_file_access_lock = asyncio.Lock()
low_level_server_shutdown_event = threading.Event()

SERVER_IP = "0.0.0.0"
SERVER_PORT = 48101
SERVER_PORT_LOW_LEVEL = 48102
DELIMITER = "<END_HEADER>"
TMP_DATA_PATH = "temp/"
DUMMY_AUDIO_PATH = "DummyAudios/"
if not os.path.exists(TMP_DATA_PATH):
    os.makedirs(TMP_DATA_PATH)
if not os.path.exists(DUMMY_AUDIO_PATH):
    os.makedirs(DUMMY_AUDIO_PATH)
IMG_WIDTH = 640
IMG_HEIGHT = (
    480  # Served as a reference, may not be equal to the actual resized image size
)
REQUEST_USER_APPROVAL_ON_CROP_IMG = True
DEFAULT_TAG_TO_WORLD_MATRIX = np.array(
    [
        [0.99842, 0.022097, 0.051611, 0.097032],
        [-0.023512, 0.99937, 0.026945, -0.33372],
        [-0.050979, -0.028121, 0.9983, 0.62143],
        [0, 0, 0, 1],
    ]
)
# mush be the same format as defined in class ObjectData
DEFAULT_USER_OBJECT = {
    "objectName": "User",
    "prefabType": "User",
    "coordinateSpace": CoordinateSpaceEnum.world,
    "layer": 1,
    "centerCoordinates": None,
    "position": [0, 0, 0],
    "orientation": [0, 0, 0, 1],
    "parent": None,
    "scale": None,
    "color": None,
}


async def send_data(websocket, data_type: str, data, extra_info: Optional[str] = None):
    """
    Send structured data to the client.
    :param websocket: WebSocket connection
    :param data_type: Type of the data (e.g., text, image, audio)
    :param data: Data payload (bytes)
    :param extra_info: for additional information, e.g., filename for image/audio
    """
    try:
        # Create JSON header
        header = {"type": data_type, "size": len(data), "extraInfo": extra_info}
        header_json = json.dumps(header)

        # Combine header and data
        header_bytes = header_json.encode("utf-8") + DELIMITER.encode("utf-8")
        combined_data = header_bytes + data

        # Send the data
        await websocket.send(combined_data)
        await asyncio.sleep(0)
        if data_type != "sync_object" and data_type != "text":
            print(f"Sent {data_type} to client.")
    except Exception as e:
        print(f"Error sending {data_type}: {e}")


async def send_dummy_audio(websocket, keyword):
    audio_file_path = DUMMY_AUDIO_PATH + keyword + ".mp3"
    # if the audio file does not exist, request from TTS model
    if not os.path.exists(audio_file_path):
        print(
            f"Audio file {audio_file_path} does not exist, generate through TTS model."
        )
        transcription = dummy_audio_transcription.get(keyword, "")
        if len(transcription) > 0:
            await request_tts_from_openai(transcription, audio_file_path)
    with open(audio_file_path, "rb") as f:
        audio_bytes = f.read()
    await send_data(websocket, "audio", audio_bytes, os.path.basename(audio_file_path))


async def process_user_registration(
    websocket, projection_matrix, camera_to_world_matrix
):
    """
    Use AprilTag to register the user's world coordinates
    """
    # Get the user's state
    state = user_states[websocket]
    tag_img = cv2.imread(TMP_DATA_PATH + f"user_{state['id']}_registry.jpg")
    tag_img_width, tag_img_height = (
        tag_img.shape[1],
        tag_img.shape[0],
    )
    if state["camera_params"] is None:
        camera_params = (F_X, F_Y, C_X, C_Y)
    else:
        camera_params = state["camera_params"]
    T_w_tag = getTagPoseToWorldFromImage(
        tag_img, tag_detector, camera_params, camera_to_world_matrix
    )
    if T_w_tag is None:
        print("Error: No tag detected.")
        await send_data(
            websocket, "registration", "No tag detected.".encode("utf-8"), "failure"
        )
        await send_dummy_audio(websocket, "FailedRegistration")
    else:
        # detect whether matrix are singular matrix
        if np.linalg.matrix_rank(camera_to_world_matrix) < 4:
            print("Error: Camera to world matrix is singular.")
            camera_to_world_matrix = np.eye(4)
        if np.linalg.matrix_rank(T_w_tag) < 3:
            print("Error: Tag to world matrix is singular.")
            T_w_tag = np.eye(4)
        T_cam_tag = np.linalg.inv(camera_to_world_matrix) @ T_w_tag
        print(
            f"User {state['id']} registered at world coordinates: \n{T_w_tag} \n tag at camera coordinates: \n{T_cam_tag}"
        )
        await send_registration_info(websocket, T_w_tag, camera_to_world_matrix)


async def send_registration_info(websocket, T_w_tag, T_w_cam=np.eye(4)):
    state = user_states[websocket]
    first_time_registration = False
    if state["registry_pose"] is None:
        first_time_registration = True
    state["registry_pose"] = T_w_tag
    # Flatten the 4x4 matrix into a 1D array
    tag_to_world_matrix_flat = T_w_tag.flatten().tolist()
    # Create a dictionary representing the data
    tag_data = {"tagScale": TAG_SIZE, "tagToWorldMatrix": tag_to_world_matrix_flat}
    # Serialize the dictionary to JSON
    tag_json = json.dumps(tag_data)
    # Add user head object id
    head_obj_id = state["id"] * (1 << 24)
    state["owned_objects"].add(head_obj_id)
    # Send client id and tag pose to the client
    await send_data(
        websocket,
        "registration",
        tag_json.encode("utf-8"),
        state["id"],
    )
    # only broadcast the tag pose to other clients if this is the first time registration
    if not first_time_registration:
        return
    # Broadcast existing client information to the new client to create objects
    for client in connected_clients:
        if client != websocket:
            target_state = user_states[client]
            if target_state["registry_pose"] is not None:
                head_obj_id = target_state["id"] * (1 << 24)
                DEFAULT_USER_OBJECT["objectName"] = "User " + str(target_state["id"])
                if sync_objects.get(head_obj_id) is not None:
                    obj_state = sync_objects[head_obj_id]
                    target_obj_data = transform_object_info(
                        obj_state, target_state, state
                    )
                    DEFAULT_USER_OBJECT["position"] = target_obj_data[:3]
                    DEFAULT_USER_OBJECT["orientation"] = target_obj_data[3:7]
                obj_str = json.dumps(DEFAULT_USER_OBJECT)
                await send_data(
                    websocket,
                    "object",
                    obj_str.encode("utf-8"),
                    "sync " + str(head_obj_id),
                )
    # Broadcast this client information to other clients
    DEFAULT_USER_OBJECT["objectName"] = "User " + str(state["id"])
    for client in connected_clients:
        if client != websocket:
            target_state = user_states[client]
            if target_state["registry_pose"] is not None:
                obj_state = (
                    T_w_cam[:3, 3].tolist()
                    + R.from_matrix(T_w_cam[:3, :3]).as_quat().tolist()
                )
                target_obj_data = transform_object_info(obj_state, state, target_state)
                DEFAULT_USER_OBJECT["position"] = target_obj_data[:3]
                DEFAULT_USER_OBJECT["orientation"] = target_obj_data[3:7]
                obj_str = json.dumps(DEFAULT_USER_OBJECT)
                await send_data(
                    client,
                    "object",
                    obj_str.encode("utf-8"),
                    "sync " + str(state["id"] * (1 << 24)),
                )


async def process_user_audio_request(websocket, audio_filename):
    # Get the user's state
    state = user_states[websocket]
    transcript = request_transcription_local(TMP_DATA_PATH + audio_filename)
    msg = f"Transcript: {transcript}"
    print(msg)
    if transcript is None:
        await send_data(websocket, "text", "No speech detected.".encode("utf-8"))
        return
    await send_data(websocket, "text", msg.encode("utf-8"))
    # Wait for the image to be received
    await state["image_event"].wait()
    await process_user_request_multi_stage(websocket, transcript)
    # Reset events for future requests
    state["image_event"].clear()


async def process_user_request_multi_stage(websocket, msg):
    # initial stage, categorize the user's request and crop the image if needed
    print(f"Sent initial request: {msg} to OpenAI, waiting for response...")
    state = user_states[websocket]
    res = request_initial_chat(msg, img_description=state["detected_objects"])
    if not res:
        print("Error: No response received.")
    elif res.refusal:
        await process_text_response(websocket, res.refusal)
    else:
        initial_response = res.parsed
        print(f"Initial response: {initial_response}")
        # Allow the initial text response to be generated first
        await process_text_response(websocket, initial_response.response)
        # Refined stages
        for sub_request in initial_response.user_requests:
            # request context data from the client
            context_request = sub_request.contextCategory.model_dump_json()
            await send_data(
                websocket, "context_request", context_request.encode("utf-8")
            )
            await state["context_data_event"].wait()
            state["context_data_event"].clear()
            res = await process_sub_request(sub_request, state, websocket)
            await process_refined_response(websocket, res, sub_request.requestCategory)


async def process_refined_response(websocket, res, req_type: RequestCategoryEnum):
    if not res:
        print("No response received. Skip current request.")
    elif type(res) == str:
        await process_text_response(websocket, res)
    elif res.refusal:
        await process_text_response(websocket, res.refusal)
    else:
        task_voice = process_text_response(websocket, res.parsed.response)
        if req_type == RequestCategoryEnum.objectCreation:
            task_generation = process_obj_creation(
                websocket, res.parsed.objectsToBeCreated
            )
        elif req_type == RequestCategoryEnum.animationCreation:
            task_generation = process_animation_creation(
                websocket, res.parsed.animationsToBeCreated
            )
        await asyncio.gather(task_voice, task_generation)


async def process_animation_creation(websocket, res):
    if res is None:
        print("No animations to create.")
        return
    # Get the user's state
    state = user_states[websocket]
    for anim in res:
        msg = f"Creating animation: {anim.animationID}"
        msg += convert_pixel_coordinates(anim, state)
        print(msg)
        print(f"Sent animation data: {anim.model_dump_json()}")
        await send_data(websocket, "animation", anim.model_dump_json().encode("utf-8"))


async def process_obj_creation(websocket, res):
    if res is None:
        print("No objects to create.")
        return
    # Get the user's state
    state = user_states[websocket]
    for obj in res:
        msg = f"Creating object: {obj.objectName}"
        msg += convert_pixel_coordinates(obj, state)
        print(msg)
        print(f"Sent object data: {obj.model_dump_json()}")
        if obj.prefabType == "WhiteboardSet":
            obj_id = assign_object_id(state["id"], state["owned_objects"], 2)
        else:
            obj_id = assign_object_id(state["id"], state["owned_objects"])
        await send_data(
            websocket, "object", obj.model_dump_json().encode("utf-8"), str(obj_id)
        )


def convert_pixel_coordinates(json_data, state):
    msg = ""
    if json_data.coordinateSpace == CoordinateSpaceEnum.pixel:
        if json_data.centerCoordinates is None:
            json_data.centerCoordinates = [
                state["resized_img_width"] / 2,
                state["resized_img_height"] / 2,
            ]
        msg += f" at resized ({json_data.centerCoordinates[0]}, {json_data.centerCoordinates[1]})"
        # convert coordinates to original image size
        x, y = json_data.centerCoordinates
        x = int(x * state["ori_img_width"] / state["resized_img_width"])
        y = int(y * state["ori_img_height"] / state["resized_img_height"])
        json_data.centerCoordinates = [x, y]
        msg += f" at original ({x}, {y})"
    return msg


async def process_text_response(websocket, res):
    msg = f"Response: {res}"
    print(msg)
    await send_data(websocket, "text", msg.encode("utf-8"))
    # only one audio response at a time as it operates on a shared file
    async with audio_file_access_lock:
        tts_audio = await request_tts_from_openai(res)
        with open(tts_audio, "rb") as f:
            tts_audio_bytes = f.read()
    await send_data(websocket, "audio", tts_audio_bytes, os.path.basename(tts_audio))


async def process_sub_request(
    sub_request: ParsedUserRequest, state: Dict, websocket=None
):
    """
    process each sub-request included in the initial response
    """
    cropped_img_path = None
    if sub_request.cropArea is not None:
        # crop the original image based on the crop area
        crop_area = sub_request.cropArea
        x_ratio = state["ori_img_width"] / state["resized_img_width"]
        y_ratio = state["ori_img_height"] / state["resized_img_height"]
        cropped_img_path = TMP_DATA_PATH + "cropped_img.jpg"
        crop_and_save_image(
            TMP_DATA_PATH + state["ori_img_filename"],
            cropped_img_path,
            int(crop_area[0] * x_ratio),
            int(crop_area[1] * y_ratio),
            int(crop_area[2] * x_ratio),
            int(crop_area[3] * y_ratio),
        )
        if REQUEST_USER_APPROVAL_ON_CROP_IMG:
            await process_crop_confirmation(websocket, "cropped_img.jpg")
            if not state["user_consent"]:
                print("User rejected the cropped image, do not continue actions.")
                return
            # clear the user consent flag for the next request
            state["user_consent"] = False
    if sub_request.contextCategory.whiteboard:
        # save the whiteboard image
        cropped_img_path = TMP_DATA_PATH + "whiteboard.png"
        await state["whiteboard_img_event"].wait()
        print("Whiteboard image received.")
        state["whiteboard_img_event"].clear()
        if REQUEST_USER_APPROVAL_ON_CROP_IMG:
            await process_crop_confirmation(websocket, "whiteboard.png")
            if not state["user_consent"]:
                print("User rejected the cropped image, do not continue actions.")
                return
            # clear the user consent flag for the next request
            state["user_consent"] = False
    return request_refined_chat(sub_request, state, cropped_img_path)


async def process_crop_confirmation(websocket, crop_img_filename):
    """
    Before uploading image to cloud server, the edge server sends the image to client, and wait for the confirmation for further process.
    """
    state = user_states[websocket]
    # Send the cropped image to the client
    with open(TMP_DATA_PATH + crop_img_filename, "rb") as f:
        crop_img_bytes = f.read()
    await send_data(websocket, "image", crop_img_bytes, crop_img_filename)
    await state["confirm_crop_image"].wait()
    print("Crop image event confirmed.")
    state["confirm_crop_image"].clear()


async def handle_client(websocket):
    websocket.transport.get_extra_info("socket").setsockopt(
        socket.IPPROTO_TCP, socket.TCP_NODELAY, 1
    )
    # Register client
    connected_clients.add(websocket)
    print(f"New client connected to WebSocket server: {websocket.remote_address}")
    # Get the user's state
    state = user_states[websocket]
    state["id"] = len(connected_clients)
    # Send audio to welcome the user
    await send_dummy_audio(websocket, "ServerConnected")
    try:
        async for message in websocket:
            # Split header and body
            data = message.split(DELIMITER.encode("utf-8"), 1)
            if len(data) < 2:
                print("Invalid message received.")
                continue

            # Parse the JSON header
            header_json, body = data[0], data[1]
            header = json.loads(header_json.decode("utf-8"))
            if header["type"] != "sync_object":
                print(f"Received header: {header}")
            msg = header["type"] + " message received."
            # Handle data based on type
            if header["type"] == "text":
                text = body.decode("utf-8")
                print(f"Received text: {text}")
            elif header["type"] == "context_data":
                context_str = body.decode("utf-8")
                print(f"Received user contextual information:\n{context_str}")
                if header["extraInfo"] == "prefab":
                    update_supported_prefab_types_with_sizes(context_str)
                else:
                    state["context_data"] = context_str
                    state["context_data_event"].set()
            elif header["type"] == "confirm_crop_image":
                user_consent = header["extraInfo"]
                print(f"User consent: {user_consent}")
                if user_consent == "Yes":
                    state["user_consent"] = True
                else:
                    await send_dummy_audio(websocket, "ImgRejected")
                # Signal that the cropped image is confirmed
                state["confirm_crop_image"].set()
            elif header["type"] == "image":
                filename = header.get("extraInfo", "received_image.jpg")
                with open(TMP_DATA_PATH + filename, "wb") as f:
                    f.write(body)
                state["ori_img_filename"] = filename
                print(f"Image saved as {filename}")
                if "whiteboard" in filename:
                    state["whiteboard_img_event"].set()
                else:
                    # resize the image and keep the original image size
                    (
                        state["ori_img_width"],
                        state["ori_img_height"],
                        state["resized_img_width"],
                        state["resized_img_height"],
                    ) = resize_and_save_image(
                        TMP_DATA_PATH + filename,
                        TMP_DATA_PATH + "resized_img.jpg",
                        IMG_WIDTH,
                        IMG_HEIGHT,
                    )
                    # Perform object detection and store the results
                    state["detected_objects"] = detect_img(
                        TMP_DATA_PATH + "resized_img.jpg"
                    )
                    state[
                        "detected_objects"
                    ] += f"The resolution of the frame is {state['resized_img_width']}x{state['resized_img_height']}."
                    # Signal that the image is ready
                    state["image_event"].set()
            elif header["type"] == "audio":
                filename = header.get("extraInfo", "received_audio.wav")
                with open(TMP_DATA_PATH + filename, "wb") as f:
                    f.write(body)
                print(f"Audio saved as {filename}")
                asyncio.create_task(process_user_audio_request(websocket, filename))
            elif header["type"] == "registration":
                # Register the user's world coordinates
                extra_info = header["extraInfo"]
                if extra_info == "VR user":
                    print("Register VR user with default tag pose.")
                    pose_lines = body.decode("utf-8").strip().split("\n")
                    cam_world_start_idx = (
                        pose_lines.index("Camera to world Matrix:") + 1
                    )
                    cam_world_lines = pose_lines[
                        cam_world_start_idx : cam_world_start_idx + 4
                    ]
                    # here the matrix directly come from Unity, no need to flip the z axis
                    camera_to_world_matrix = np.array(
                        [list(map(float, line.split())) for line in cam_world_lines]
                    )
                    await send_registration_info(
                        websocket, DEFAULT_TAG_TO_WORLD_MATRIX, camera_to_world_matrix
                    )
                else:
                    regi_path = TMP_DATA_PATH + f"user_{state['id']}_registry.jpg"
                    with open(regi_path, "wb") as f:
                        f.write(body)
                    # parse the pose info to get projection_matrix and camera_to_world_matrix
                    # Split the string into lines
                    pose_lines = extra_info.strip().split("\n")
                    # Find indices for Projection Matrix and Camera to World Matrix
                    proj_start_idx = pose_lines.index("Projection Matrix:") + 1
                    cam_world_start_idx = (
                        pose_lines.index("Camera to world Matrix:") + 1
                    )
                    # Extract the projection matrix lines and camera-to-world matrix lines
                    proj_lines = pose_lines[proj_start_idx : proj_start_idx + 4]
                    cam_world_lines = pose_lines[
                        cam_world_start_idx : cam_world_start_idx + 4
                    ]
                    # Convert the lines to numpy arrays
                    projection_matrix = np.array(
                        [list(map(float, line.split())) for line in proj_lines]
                    )
                    camera_to_world_matrix = np.array(
                        [list(map(float, line.split())) for line in cam_world_lines]
                    )
                    print(
                        f"Projection Matrix:\n{projection_matrix}\n camera to world matrix:\n{camera_to_world_matrix}"
                    )
                    if extra_info.startswith("Quest"):
                        # update the camera intrinsics for Quest
                        cam_intrinsics_line = pose_lines.index("Camera Intrinsics:") + 1
                        state["camera_params"] = tuple(
                            map(float, pose_lines[cam_intrinsics_line].split())
                        )
                        print(f"Camera intrinsics: {state['camera_params']}")
                    else:
                        # For HoloLens, the received camera to world matrix needs to flip the z axis to match Unity's coordinate system
                        camera_to_world_matrix[:3, 2] *= -1
                    asyncio.create_task(
                        process_user_registration(
                            websocket, projection_matrix, camera_to_world_matrix
                        )
                    )
            elif header["type"] == "object":
                # get the client id that created the object
                owner_id = state["id"]
                object_id = int(header["extraInfo"])
                # object data received from client
                obj_data = json.loads(body.decode("utf-8"))
                print(f"Received object data: {obj_data} from client {owner_id}")
                # broadcast the object data to all clients except the sender
                for client in connected_clients:
                    if client != websocket:
                        # check if the client is still connected
                        if client.state != websockets.protocol.State.OPEN:
                            print(f"Client {client.remote_address} is disconnected.")
                            continue
                        target_state = user_states[client]
                        # only synchronize registered users
                        if user_states[client]["registry_pose"] is None:
                            print(f"Client {target_state['id']} is not registered.")
                            continue
                        print(
                            f"Forwarding object data to client {client.remote_address} with id {target_state['id']}"
                        )
                        # transform the position and orientation of the object to the target client's world coordinate system
                        new_obj_data = transform_object_json(
                            obj_data, state, target_state
                        )
                        obj_str = json.dumps(new_obj_data)
                        await send_data(
                            client,
                            "object",
                            obj_str.encode("utf-8"),
                            "sync " + str(object_id),
                        )
            elif header["type"] == "animation":
                # get the client id that created the animation
                owner_id = state["id"]
                # animation data received from client
                anim_data = json.loads(body.decode("utf-8"))
                print(f"Received animation data: {anim_data} from client {owner_id}")
                # broadcast the animation data to all clients except the sender
                for client in connected_clients:
                    if client != websocket:
                        # check if the client is still connected
                        if client.state != websockets.protocol.State.OPEN:
                            print(f"Client {client.remote_address} is disconnected.")
                            continue
                        target_state = user_states[client]
                        # only synchronize registered users
                        if target_state["registry_pose"] is None:
                            print(f"Client {target_state['id']} is not registered.")
                            continue
                        print(
                            f"Forwarding animation data to client {client.remote_address} with id {target_state['id']}"
                        )
                        # transform world position and orientation if the animation includes related properties
                        new_anim_data = transform_object_json(
                            anim_data, state, target_state
                        )
                        anim_str = json.dumps(new_anim_data)
                        await send_data(
                            client,
                            "animation",
                            anim_str.encode("utf-8"),
                            "sync",
                        )
            elif header["type"] == "sync_object":
                # body is a bytes object, parse the 44 bytes
                # (int32) + 10 floats => struct format: i 10f
                object_id, px, py, pz, rx, ry, rz, rw, sx, sy, sz = struct.unpack(
                    "i10f", body
                )
                object_state = [px, py, pz, rx, ry, rz, rw, sx, sy, sz]
                sync_objects[object_id] = object_state
                extra_info = header["extraInfo"]
                # broadcast the object state to all clients except the sender
                for client in connected_clients:
                    if client != websocket:
                        # check if the client is still connected
                        if client.state != websockets.protocol.State.OPEN:
                            print(f"Client {client.remote_address} is disconnected.")
                            continue
                        target_state = user_states[client]
                        # only synchronize registered users
                        if target_state["registry_pose"] is None:
                            print(f"Client {target_state['id']} is not registered.")
                            continue
                        # transform world position and orientation if the object includes related properties
                        target_obj_data = transform_object_info(
                            object_state, state, target_state
                        )
                        target_obj_data = (
                            [object_id] + target_obj_data + object_state[7:]
                        )
                        # pack the data into bytes
                        obj_bytes = struct.pack("i10f", *target_obj_data)
                        await send_data(client, "sync_object", obj_bytes, extra_info)
            elif header["type"] == "control":
                control_msg = header["extraInfo"]
                print(f"Received control message: {control_msg}")
                if control_msg == "clearMsg":
                    clear_user_messages()
                    print("User message history cleared.")
            else:
                print("Unknown data type.")
            await send_data(websocket, "text", msg.encode("utf-8"))
    except websockets.ConnectionClosed:
        print(f"Client disconnected: {websocket.remote_address}")
    finally:
        connected_clients.remove(websocket)


# %% low-level socket server for faster forwarding of syncchonized objects
def low_level_socket_server():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind((SERVER_IP, SERVER_PORT_LOW_LEVEL))
        s.settimeout(1.0)  # Allow periodic checks for shutdown
        s.listen()
        print(f"Low-level socket server started on {SERVER_IP}:{SERVER_PORT_LOW_LEVEL}")
        while not low_level_server_shutdown_event.is_set():
            try:
                conn, addr = s.accept()
                # Disable Nagle's algorithm for low latency
                conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            except socket.timeout:
                continue  # Check shutdown_event again
            threading.Thread(
                target=handle_socket_client, args=(conn, addr), daemon=True
            ).start()
        print("Low-level socket server shutting down.")
        s.close()


def handle_socket_client(conn, addr):
    with conn:
        connected_clients_low_level.add(conn)
        print(f"Low-level socket server connected by {addr}")
        while True:
            try:
                data = conn.recv(1024)
            except Exception:
                break
            if not data:
                break
            # Broadcast the data to all other socket clients
            for client in connected_clients_low_level:
                if client != conn:
                    try:
                        client.sendall(data)
                    except Exception:
                        print(f"Client disconnection detected.")
        print(f"Low-level socket server disconnected by {addr}")
        connected_clients_low_level.remove(conn)


# %% main server script
async def main():
    # Set max size of messages to 10MB
    server = await websockets.serve(
        handle_client, SERVER_IP, SERVER_PORT, max_size=1024 * 1024 * 10
    )
    print("WebSocket server started on ws://" + SERVER_IP + ":" + str(SERVER_PORT))

    # Start low-level socket server in background thread
    low_level_socket_thread = threading.Thread(
        target=low_level_socket_server, daemon=True
    )
    low_level_socket_thread.start()

    try:
        # Run until cancelled
        await asyncio.Future()
    except asyncio.CancelledError:
        print("WebSocket server shutting down...")
    finally:
        # Signal the socket server to stop
        low_level_server_shutdown_event.set()
        low_level_socket_thread.join(timeout=2)
        print("Socket server terminated.")


if __name__ == "__main__":
    tag_detector = getAprilTagDetector()
    asyncio.run(main())
