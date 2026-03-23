"""
This script maintains the connection to the OpenAI API, all the requests to the API are made through this script.
"""

from dotenv import load_dotenv
from pydantic import BaseModel, Field
from openai import OpenAI, AsyncOpenAI
from typing import List, Optional, Dict
import asyncio
import os
import base64
from enum import Enum
from utils import (
    crop_and_save_image,
    get_user_message_context,
    enqueue_user_message,
)

SUPPORTED_PREFAB_TYPES: Dict[str, str] = {}
MODEL_TYPE = "gpt-4o"
TMP_DATA_PATH = "temp/"
if not os.path.exists(TMP_DATA_PATH):
    os.makedirs(TMP_DATA_PATH)


# Initialize the OpenAI API
current_file_path = os.path.abspath(__file__)
current_directory = os.path.dirname(current_file_path)
# load the .env file containing your API key
load_dotenv(dotenv_path=current_directory + "/.env")
api = OpenAI()
api_async = AsyncOpenAI()


def update_supported_prefab_types_with_sizes(data: str):
    """
    Parse the input string and update SUPPORTED_PREFAB_TYPES
    with names as keys and sizes as values.
    """
    if not data:
        return
    global SUPPORTED_PREFAB_TYPES
    SUPPORTED_PREFAB_TYPES = {}

    lines = data.strip().split("\n")
    if not lines:
        return
    for line in lines:
        if not line:
            continue
        name, size = line.rsplit(" ", 1)
        name = name.strip()
        size = size.strip("()")  # Remove parentheses
        SUPPORTED_PREFAB_TYPES[name] = size


# %% Object Creation
class CoordinateSpaceEnum(str, Enum):
    pixel = "pixel"
    world = "world"
    local = "local"


# Define the model for creating objects
class ObjectData(BaseModel):
    objectName: str
    prefabType: str
    coordinateSpace: CoordinateSpaceEnum = Field(
        ...,
        description="This property determines the reference frame for positioning objects based on the provided context. If the position is derived from detection results tied to the user's Field of View or directly related to the user's Field of View, use pixel space. When the scene includes contextual 3D positions of other objects, allowing direct placement in the environment, use world space. If the position is defined relative to a parent object within a hierarchical structure, use local space. Use pixel space instead of local space if both hierarchical structure and image related contextx exist",
    )
    layer: int = Field(
        ..., description="A specified layer number to classify objects, starts with 0"
    )
    centerCoordinates: Optional[List[int]] = Field(
        None, description="Format: (x, y), only applicable for pixel coordinate space"
    )
    position: Optional[List[float]] = Field(
        None,
        description="Format: (x, y, z), use world position for world coordinate space, use local position for local coordinate space",
    )
    orientation: Optional[List[float]] = Field(
        None,
        description="Format: (x, y, z, w), not applicable for pixel coordinate system",
    )
    parent: Optional[str] = Field(
        None,
        description="The parent object of the current object. Case sensitive. Must be the virtual objects associated to the scene, cannot be objects in the image description",
    )
    scale: Optional[List[float]] = Field(None, description="Format: (x, y, z)")
    color: Optional[List[float]] = Field(
        None, description="Format: (r, g, b), Range: 0-1"
    )


# Define the model for the overall response
class ObjectCreationResponse(BaseModel):
    response: str
    # List of objects with required properties
    objectsToBeCreated: Optional[List[ObjectData]] = None


# %% Animation Creation
class AnimationActionTypeEnum(str, Enum):
    attach = "attach"
    detach = "detach"
    scale = "scale"
    color = "color"
    movetowards = "movetowards"
    rotatetowards = "rotatetowards"
    looktowards = "looktowards"
    selfrotate = "selfrotate"
    orbit = "orbit"
    gazing = "gazing"
    stop = "stop"
    remove = "remove"
    grabbable = "grabbable"


class AnimationData(BaseModel):
    actionType: AnimationActionTypeEnum = Field(
        ...,
        description="""The type of animation to be applied to the object.
                    attach: Attach an object to the other object.
                    detach: Detach an object from its current parent.
                    scale: Adjust the scale of an object.
                    color: Change the color of an object.
                    movetowards: Move an object to a specific position or the other object's position.
                    rotatetowards: Rotate an object to a specific orientation. Also support degrees of rotation by providing rotating axis, speed and time.
                    looktowards: Rotate an object to face with or look at a specific position or the other object's position.
                    selfrotate: Let an object be rotating with a specific axis.
                    orbit: Let an object be orbiting around the other object.
                    gazing: Let an object be facing at the other object. To use gazing, there must be a proper virtual object associated to the scene (intead of objects in user's FoV) in the context to serve as the target object, otherwise, use looktowards.
                    stop: Stop the animation with the specified id. Make sure the id is shown exactly in the animation context.
                    remove: Remove an existing object from the scene by its name.
                    grabbable: Make an object grabbable, i.e., to be interacted with user hand.""",
    )
    objectName: str = Field(
        ...,
        description="The name of the object to which the action will be applied. Case sensitive. Must be exactly the same as shown in the context instead of the user request.",
    )
    animationID: str = Field(
        ...,
        description="A unique string to identify the animation. Either generate a new string or use exactly the same as shown in the contextual information.",
    )
    newObjectName: Optional[str] = Field(
        None,
        description="The new name of the object if some properties like the color is changed after the action. Required for 'color' action.",
    )
    duration: Optional[float] = Field(
        None,
        description="The duration over which the action should be completed or ended, in seconds.",
    )
    target: Optional[str] = Field(
        None,
        description="The other object's name for an animation involving multiple objects. Serves as a parent object for some animations. Must be exactly the same as shown in the context instead of the user request.",
    )
    scale: Optional[List[float]] = Field(
        None, description="New scale of the object, specified as 'x y z'."
    )
    color: Optional[List[float]] = Field(
        None,
        description="New color of the object, specified as 'r g b'. Values are with a range from 0 to 1.",
    )
    coordinateSpace: Optional[CoordinateSpaceEnum] = Field(
        ...,
        description="This property determines the reference frame for positioning objects based on the provided context. If the position is derived from detection results tied to the user's Field of View or directly related to the user's Field of View, use pixel space. When the scene includes contextual 3D positions of other objects, allowing direct placement in the environment, use world space. If the position is defined relative to a parent object within a hierarchical structure, use local space. Use pixel space instead of local space if both hierarchical structure and image related contextx exist",
    )
    centerCoordinates: Optional[List[int]] = Field(
        None, description="Format: (x, y), only applicable for pixel coordinate space"
    )
    position: Optional[List[float]] = Field(
        None,
        description="New position of the object in world coordinate, specified as 'x y z', for movement actions and 'looktowards' action.",
    )
    localposition: Optional[List[float]] = Field(
        None,
        description="New position of the object in local coordinate, specified as 'x y z'. If not specified the other object, refer to the coordinate of the object itself. Otherwise, refer to the coordinate of the other object.",
    )
    localdirection: Optional[List[float]] = Field(
        None,
        description="The direction in which the action will take place, like moving in a specific direction.",
    )
    distance: Optional[float] = Field(
        None,
        description="The distance to move the object, used in movement-related animations with a specified direction.",
    )
    safebound: Optional[float] = Field(
        None,
        description="The safe distance to avoid collision with other objects. This can be a negative number, which will leverage the rendering information instead of an absolute value.",
    )
    orientation: Optional[List[float]] = Field(
        None,
        description="New orientation of the object, specified as 'x y z w', for rotational actions.",
    )
    axis: Optional[List[float]] = Field(
        None,
        description="Axis along which to rotate, specified as 'x y z', for the 'selfrotate' action, e.g., [0,1,0].",
    )
    speedRot: Optional[float] = Field(
        None,
        description="Rotation speed in degrees per second, used in rotation-related animations, e.g., 90.",
    )
    speedMov: Optional[float] = Field(
        None,
        description="Movement speed in meters per second, used in movement-related animations, e.g., 1.",
    )


class AnimationCreationResponse(BaseModel):
    response: str
    # List of animations with required properties
    animationsToBeCreated: Optional[List[AnimationData]] = None


# %% Initial Chat
class RequestCategoryEnum(str, Enum):
    chat = "chat"
    objectCreation = "objectCreation"
    animationCreation = "animationCreation"


class ContextCategory(BaseModel):
    position: bool = Field(..., description="Whether the object position is required")
    orientation: bool = Field(
        ..., description="Whether the object orientation is required"
    )
    scale: bool = Field(..., description="Whether the object scale is required")
    size: bool = Field(..., description="Whether the object size is required")
    animationData: bool = Field(
        ...,
        description="Set to true if and only if the category of the request is animationCreation",
    )
    user: bool = Field(
        ...,
        description="Whether the contextual information associtated to the user is required",
    )
    whiteboard: bool = Field(
        ...,
        description="Whether the contextual information associtated to a whiteboard is required, e.g., recognize user's writing or drawing. But this should be set to false when creating a whiteboard.",
    )


class ParsedUserRequest(BaseModel):
    request: str = Field(..., description="User's request")
    cropArea: Optional[List[int]] = Field(
        None,
        description="Format: (x1, y1, x2, y2), where (x1, y1) is the top-left corner and (x2, y2) is the bottom-right corner. Set this to None if the textual description is sufficient to meet user's request, e.g., center coordinate is sufficient to derive the position.",
    )
    requestCategory: RequestCategoryEnum = Field(
        ...,
        description="Category of the user's request, create ojects are object creation requests, remove or edit property of objects and create animations are treated as animation creation requests, all other requests are chat requests.",
    )
    contextCategory: ContextCategory = Field(
        ..., description="Determine the required context for the request"
    )


class InitialResponse(BaseModel):
    response: str = Field(
        ...,
        description="Should be a quick response to let the user know you are analyzing the request",
    )
    user_requests: List[ParsedUserRequest] = Field(
        ..., description="List of user's requests"
    )


# %% General request functions
async def request_tts_from_openai(
    text, speech_file_path=TMP_DATA_PATH + "/ttsAudio.mp3"
):
    response = await api_async.audio.speech.create(
        model="tts-1",
        voice="alloy",
        input=text,
    )
    response.stream_to_file(speech_file_path)
    return speech_file_path


def request_transcription_from_openai(audio_file_path):
    transcript = api.audio.transcriptions.create(
        model="whisper-1", file=open(audio_file_path, "rb"), language="en"
    )
    return transcript.text


def request_initial_chat(message, img_description):
    # add current request to the user's message list
    enqueue_user_message(message)
    res_format = InitialResponse
    sys_msg = f"""You are an assistant in a Mixed Reality application.
        You are expected to process the user's request and generate sub-requests in specified format.
        The user message is acquired via audio transcription, sometimes the transcription may not be accurate, you should correct the obvious mistakes considering the context when generate sub-requests.
        But do not change the meaning of the user's request, e.g., if the user wants to update the properties of an object, do not try to create a new object.
        The size of the list can be 1 if the request is simple, or more if the request is complex.
        Complex requests are defined as those that involve multiple categories, those should be split into a list of sub-requests. Otherwise, the list should contain only one sub-request.
        For example, a request to create multiple objects or create multiple animation are not complex request and should be within one sub-request.
        But creating an object followed by an animation is a complex request and should be divided into multiple sub-requests.
        Be careful about the order of the sub-requests, e.g., create a grabbable object should be split into first request to create the object, then second request to create grabbable animation.
        Ensure generated sub-request has not been executed previously. For example, when user wants to create an animation, the objects may already be created previously and do not create it again.
        {get_user_message_context()}
        The following is the description of the user's field of view, includes the objects detected in the image with names, their center coordinates (x,y), bounding boxes (x1,y1,x2,y2), and confidence scores for the detection
        {img_description}
        Note that the name of the objects may not be accurate, you should associate similar objects.
        If the user's request need information from the raw image, e.g., content of a book or color of objects, you should return the crop area of the image based on the detected objects. 
        For instance, the user request a translation of the text on the book, you should return the crop area of the book. Or a user require properties not included in the description, like color of an object, you should return the crop area of that object.
        If the information in the description is enough, you should return None as the crop area to save resources.
        For instance, if the user's request is to move an object to specific position in the view or objects already detected, crop area is None.
        Besides, carefully review the description for the context category, determine which category of context is required to satisfy user's request.
        """
    return request_chat_from_openai(message, sys_msg, res_format)


def request_refined_chat(
    user_request: ParsedUserRequest, state: Dict, img_path: Optional[str] = None
):
    if user_request.requestCategory == RequestCategoryEnum.chat:
        print(f"Processing instruction: {user_request.request}")
        return request_refined_chat_instruction(
            user_request, state["detected_objects"], img_path, state["context_data"]
        )
    elif user_request.requestCategory == RequestCategoryEnum.objectCreation:
        print(f"Processing object creation: {user_request.request}")
        return request_refined_chat_object(
            user_request, state["detected_objects"], img_path, state["context_data"]
        )
    elif user_request.requestCategory == RequestCategoryEnum.animationCreation:
        print(f"Processing animation creation: {user_request.request}")
        return request_refined_chat_animation(
            user_request, state["detected_objects"], img_path, state["context_data"]
        )


def request_refined_chat_object(
    user_request: ParsedUserRequest,
    img_description: str,
    img_path: Optional[str] = None,
    context_data: Optional[str] = None,
):
    res_format = ObjectCreationResponse
    sys_msg = f"""You are an assistant in a Mixed Reality application.
            You are expected to create virtual objects based on the user's request and the following contextual information.
            {context_data}
            {get_user_message_context()}
            Here is the description of the user's field of view:
            {img_description}
            The description includes the objects detected in the image with names, their coordinates, bounding boxes, and confidence scores for the detection.
            Note that the name of the objects may not be accurate due to voice recognition issue, you should associate similar objects.
            Based on the user's request, you may need to provide formatted data to create virtual objects within her field of view. The following are the supported object types and their sizes when scale=1:
            {", ".join(
                f"{name}: ({size})" for name, size in SUPPORTED_PREFAB_TYPES.items()
            )}
            Note that Cube, Sphere, Cyliner, Capsule and Quad are Unity Primitives, all others are prefabs.
            When creating objects, always prefer to identify the closest matching prefab type. If no reasonable match exists, use primitives to approximate the desired shape and structure.
            When creating whiteboards, use given whiteboard prefab instead of Unity Primitives like Quad. Also, associate normal voice recoginition issues like white bar or red board to whiteboard.
            If the position of object can be derived from the image or image description, use pixel coordinate space.
            Assign layers systematically: floor and ceiling objects should be in layer 0, objects resting on the floor belong to layer 1, and child objects should be placed in a layer that is one level higher than their parent.
            When generating multiple objects, ensure that those at lower layers are created first to maintain a logical structure.
            For complex objects, start with an empty base object and attach additional components as its children to maintain hierarchy and organization.
            Note that in the coordinate system, x-axis points to the right, y-axis points up, and z-axis points forward.
            """
    if img_path is None:
        sys_msg += " You are also provided with an image of the user's field of view, try to extract helpful information from it."
    return request_chat_from_openai(user_request.request, sys_msg, res_format, img_path)


def request_refined_chat_instruction(
    user_request: ParsedUserRequest,
    img_description: str,
    img_path: Optional[str] = None,
    context_data: Optional[str] = None,
):
    res_format = None
    sys_msg = f"""You are an assistant in a Mixed Reality application.
            You are expected to provide helpful information for the user given her request and the following description of her field of view (FoV):
            {img_description}
            The description includes the objects detected in the FoV image with names, their coordinates, bounding boxes, and confidence scores for the detection.
            Note that the name of the objects may not be accurate, you should associate similar objects.
            When user asks a description of the view, provide only a very brief description with the number and name of the detected objects unless the user asks for more details.
            {context_data}
            {get_user_message_context()}
            """
    if img_path is None:
        sys_msg += "\nYou are also provided with an image, try to extract helpful information from it. Note that this is a cropped area of the original image for the user's FoV."
    return request_chat_from_openai(user_request.request, sys_msg, res_format, img_path)


def request_refined_chat_animation(
    user_request: ParsedUserRequest,
    img_description: str,
    img_path: Optional[str] = None,
    context_data: Optional[str] = None,
):
    res_format = AnimationCreationResponse
    sys_msg = f"""You are an assistant in a Mixed Reality application.
            You are expected to create animations based on the user's request and the following contextual information.
            {context_data}
            {get_user_message_context()}
            Here is the description of the user's field of view:
            {img_description}
            The description includes the objects detected in the image with names, their coordinates, bounding boxes, and confidence scores for the detection.
            Note that the name of the objects may not be accurate, you should associate similar objects.
            If the request seems to change the properties of existing animaions in the context, stop the existing animation first and then create a new one.
            """
    if img_path is None:
        sys_msg += " You are also provided with an image of the user's field of view, try to extract helpful information from it."
    return request_chat_from_openai(user_request.request, sys_msg, res_format, img_path)


def request_chat_from_openai(user_request, sys_msg, res_format=None, img_path=None):
    if img_path is not None:
        if not os.path.exists(img_path):
            print(f"Error: Image file not found at {img_path}")
            return
        with open(img_path, "rb") as image_file:
            base64_image = base64.b64encode(image_file.read()).decode("utf-8")
        content = [
            {"type": "text", "text": user_request},
            {
                "type": "image_url",
                "image_url": {"url": f"data:image/jpeg;base64,{base64_image}"},
            },
        ]
    else:
        content = user_request
    msg = [
        {"role": "system", "content": sys_msg},
        {
            "role": "user",
            "content": content,
        },
    ]
    if res_format is not None:
        response = api.beta.chat.completions.parse(
            model=MODEL_TYPE,
            messages=msg,
            response_format=res_format,
        )
        return response.choices[0].message
    else:
        response = api.chat.completions.create(
            model=MODEL_TYPE,
            messages=msg,
        )
        return response.choices[0].message.content


# %% Test the functions
if __name__ == "__main__":
    import time

    test_types = """
    Cube (1.000,1.000,1.000)
    Sphere (1.000,1.000,1.000)
    Cylinder (1.000,2.000,1.000)
    Capsule (1.000,2.000,1.000)
    Quad (1.000,1.000,0.000)
    Calculator (0.129,0.028,0.199)
    Computer (0.219,0.484,0.516)
    ComputerChair (0.675,1.113,0.753)
    ComputerDesk (1.906,0.813,0.906)
    Eraser (0.079,0.017,0.032)
    Keyboard (0.563,0.039,0.188)
    Laptop (0.391,0.259,0.316)
    Monitor (0.781,0.688,0.234)
    Pen black (0.023,0.018,0.378)
    Scissors black (0.306,0.013,0.132)
    Sofa (2.313,1.063,1.000)"""
    update_supported_prefab_types_with_sizes(test_types)

    from objectDetection import detect_img

    description = detect_img(TMP_DATA_PATH + "testRec.jpg")
    initial_request_time = time.time()
    res_initial = request_initial_chat(
        "Add a pencil to the monitor and move the cube towards the chair.",
        img_description=description,
    )
    initial_response_time = time.time()
    print(f"Response: {res_initial}")
    print(f"Initial Request Time taken: {initial_response_time - initial_request_time}")

    state = {
        "ori_img_width": 640,
        "ori_img_height": 480,
        "resized_img_width": 640,
        "resized_img_height": 480,
        "ori_img_filename": "testRec.jpg",
        "detected_objects": description,
        "context_data": """The following is the contextual data associated to the scene, including the object name, position, size.
            The following are the contextual data associated to the virtual objects:
            TinyCube (0.00, 0.50, 1.00)   (0.10, 0.10, 0.10)
            TinySphere (0.00, 0.51, 1.00)   (0.01, 0.01, 0.01)
            The following is the position of the user:
            (0.00, 0.00, 0.00)""",
    }
    for request in res_initial.parsed.user_requests:
        cropped_img_path = None
        if request.cropArea is not None:
            crop_area = request.cropArea
            cropped_img_path = TMP_DATA_PATH + "cropped_img.jpg"
            crop_and_save_image(
                TMP_DATA_PATH + state["ori_img_filename"],
                cropped_img_path,
                crop_area[0],
                crop_area[1],
                crop_area[2],
                crop_area[3],
            )
        refined_request_time = time.time()
        res = request_refined_chat(request, state, cropped_img_path)
        refined_response_time = time.time()
        print(f"Response: {res}")
        print(
            f"Refined Request Time taken: {refined_response_time - refined_request_time}"
        )
