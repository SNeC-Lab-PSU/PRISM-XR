"""
Some utility functions
"""

from collections import deque
import cv2
import numpy as np
from scipy.spatial.transform import Rotation as R
from typing import Tuple
import threading

MAX_MESSAGE_CAPACITY = 10 + 1  # Maximum number of messages to store in the queue
USER_MESSAGES_QUEUE = deque(maxlen=MAX_MESSAGE_CAPACITY)
USER_QUEUE_LOCK = threading.Lock()


def get_user_message_count():
    with USER_QUEUE_LOCK:
        # exclude the current message and Ensure no negative values
        return max(0, len(USER_MESSAGES_QUEUE) - 1)


def get_user_messages():
    with USER_QUEUE_LOCK:
        # Exclude the most recent message (assumed to be last)
        messages = list(USER_MESSAGES_QUEUE)[:-1][::-1]
        return "\n".join(messages)


def get_user_message_context():
    if get_user_message_count() <= 0:
        return ""
    return f"""The following are the previous {get_user_message_count()} messages from the user, from the latest to the oldest:
        {get_user_messages()}
        Those commands are already executed, just provided for richer contextual data"""


def clear_user_messages():
    with USER_QUEUE_LOCK:
        USER_MESSAGES_QUEUE.clear()


def enqueue_user_message(message: str):
    with USER_QUEUE_LOCK:
        USER_MESSAGES_QUEUE.append(message)


def transform_coordinates(
    ori_position: np.ndarray,
    ori_orientation: np.ndarray,
    T_ori_tag: np.ndarray,
    T_target_tag: np.ndarray,
) -> Tuple[np.ndarray, np.ndarray]:
    T_target_created = T_target_tag @ np.linalg.inv(T_ori_tag)
    position_target = T_target_created[:3, :3] @ ori_position + T_target_created[:3, 3]
    orientation_target = T_target_created[:3, :3] @ ori_orientation
    return position_target, orientation_target


def transform_object_json(
    owner_json: dict,
    owner_state: dict,
    target_state: dict,
) -> dict:
    has_position = "position" in owner_json and owner_json["position"] is not None
    has_orientation = (
        "orientation" in owner_json and owner_json["orientation"] is not None
    )
    if has_position:
        position_created = np.array(owner_json["position"])
    else:
        position_created = np.zeros(3)
    if has_orientation:
        orientation_created = R.from_quat(owner_json["orientation"]).as_matrix()
    else:
        orientation_created = np.eye(3)
    if not has_position and not has_orientation:
        return owner_json
    T_owner_tag = owner_state["registry_pose"]
    T_target_tag = target_state["registry_pose"]
    position_target, orientation_target = transform_coordinates(
        position_created,
        orientation_created,
        T_owner_tag,
        T_target_tag,
    )
    orientation_target_quat = R.from_matrix(orientation_target).as_quat()
    new_obj_data = owner_json.copy()
    if has_position:
        new_obj_data["position"] = position_target.tolist()
    if has_orientation:
        new_obj_data["orientation"] = orientation_target_quat.tolist()
    return new_obj_data


def transform_object_info(
    owner_obj_data: list,
    owner_state: dict,
    target_state: dict,
) -> list:
    """
    The input should be at least length of 7, including 3D position and 4D orientation in quaternion format.
    The output contains only 3D position and 4D orientation in quaternion format
    """
    position_created = np.array(owner_obj_data[:3])
    orientation_created = R.from_quat(owner_obj_data[3:7]).as_matrix()
    T_owner_tag = owner_state["registry_pose"]
    T_target_tag = target_state["registry_pose"]
    position_target, orientation_target = transform_coordinates(
        position_created,
        orientation_created,
        T_owner_tag,
        T_target_tag,
    )
    orientation_target_quat = R.from_matrix(orientation_target).as_quat()
    target_obj_data = position_target.tolist() + orientation_target_quat.tolist()
    return target_obj_data


def resize_and_save_image(input_path, output_path, target_width, target_height):
    # Load the input image
    img = cv2.imread(input_path)

    if img is None:
        print("Error: Unable to load image.")
        return

    # Check ratio of width and height
    if img.shape[1] / img.shape[0] != target_width / target_height:
        target_height = int(img.shape[0] * target_width / img.shape[1])
    # Resize the image
    resized_img = cv2.resize(
        img, (target_width, target_height), interpolation=cv2.INTER_LINEAR
    )

    # Save the resized image to the specified output file
    cv2.imwrite(output_path, resized_img)
    print(f"Resized image saved to {output_path}")
    # return orignal image width and height; and resized image width and height
    return img.shape[1], img.shape[0], target_width, target_height


# The coordinates must match the input image, be careful of the resizing
def crop_and_save_image(input_path, output_path, x1, y1, x2, y2):
    # Load the input image
    img = cv2.imread(input_path)

    if img is None:
        print("Error: Unable to load image.")
        return

    # Crop the image based on the provided coordinates
    cropped_img = img[y1:y2, x1:x2]
    # Save the cropped image to the specified output file
    cv2.imwrite(output_path, cropped_img)


def assign_object_id(client_id: int, owned_objs: set, num_childs=0) -> int:
    cap = 2**24
    start_id = client_id * cap + 1
    end_id = (client_id + 1) * cap

    # set maximum number of tries to avoid infinite loop
    max_tries = 100
    while True and max_tries > 0:
        obj_id = np.random.randint(start_id, end_id - num_childs)
        all_childs_qualified = True
        for i in range(num_childs + 1):
            if obj_id + i in owned_objs:
                all_childs_qualified = False
                break
        if all_childs_qualified:
            owned_objs.add(obj_id)
            return obj_id
        max_tries -= 1
    print("Error: Unable to assign object ID.")
    return None


def compute_iou(rect1, rect2) -> float:
    """
    Compute the Intersection over Union (IoU) between two rectangles.

    Args:
        rect1: List or Array [x1, y1, x2, y2] representing the first rectangle.
        rect2: List or Array [x3, y3, x4, y4] representing the second rectangle.

    Returns:
        float: The IoU value between the two rectangles.
    """
    # Compute intersection coordinates
    x_left = max(rect1[0], rect2[0])
    y_top = max(rect1[1], rect2[1])
    x_right = min(rect1[2], rect2[2])
    y_bottom = min(rect1[3], rect2[3])

    # Compute intersection area
    inter_width = max(0, x_right - x_left)
    inter_height = max(0, y_bottom - y_top)
    inter_area = inter_width * inter_height

    # Compute individual areas
    area1 = (rect1[2] - rect1[0]) * (rect1[3] - rect1[1])
    area2 = (rect2[2] - rect2[0]) * (rect2[3] - rect2[1])

    # Compute union area
    union_area = area1 + area2 - inter_area

    # Compute IoU (handle division by zero)
    return inter_area / union_area if union_area > 0 else 0.0


def calculate_distance(point1, point2) -> float:
    """
    Calculate the Euclidean distance between two points.

    Args:
        point1: The first point.
        point2: The second point.

    Returns:
        float: The Euclidean distance between the two points.
    """
    if len(point1) != len(point2):
        return np.inf
    return np.linalg.norm(np.array(point1) - np.array(point2))
