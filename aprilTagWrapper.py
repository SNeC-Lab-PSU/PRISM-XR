from pupil_apriltags import Detector
import cv2
import numpy as np
import matplotlib.pyplot as plt

# Change of basis that aligns the AprilTag coordinate system with the Unity coordinate system
BASIS_CHANGE = np.array(
    [
        [1, 0, 0],
        [0, -1, 0],
        [0, 0, 1],
    ]
)
R_ALIGN = np.array(
    [
        [1, 0, 0],
        [0, 0, 1],
        [0, -1, 0],
    ]
)
TAG_SIZE = 0.1
# Camera parameters for HoloLens 2 with highest resolution 3904x2196
F_X, F_Y, C_X, C_Y = 2920.3, 2909.9, 1942.8, 1063.3


def drawTagInfo(frame, detection):
    # Extract corners, center, and other details
    corners = detection.corners.astype(int)
    center = tuple(detection.center.astype(int))
    tag_id = detection.tag_id

    # Calculate scaling factor based on image resolution
    height, width = frame.shape[:2]
    scale_factor = max(height, width) / 1000.0

    # Draw corners as a polygon
    cv2.polylines(
        frame,
        [corners],
        isClosed=True,
        color=(0, 255, 0),
        thickness=int(2 * scale_factor),
    )

    # Draw the center point
    cv2.circle(
        frame, center, radius=int(5 * scale_factor), color=(0, 0, 255), thickness=-1
    )

    # Display the Tag ID near the center
    cv2.putText(
        frame,
        f"ID: {tag_id}",
        (center[0] + int(10 * scale_factor), center[1] - int(10 * scale_factor)),
        cv2.FONT_HERSHEY_SIMPLEX,
        0.5 * scale_factor,
        (125, 125, 125),
        int(2 * scale_factor),
    )
    return frame


def getCameraParameters(projection_matrix, width, height):
    # Extract intrinsics
    f_x = (width / 2.0) * projection_matrix[0, 0]
    f_y = (height / 2.0) * projection_matrix[1, 1]

    c_x = (width / 2.0) * (1.0 + projection_matrix[0, 2])
    c_y = (height / 2.0) * (1.0 + projection_matrix[1, 2])

    camera_params = (f_x, f_y, c_x, c_y)
    return camera_params


def getAprilTagDetector():
    return Detector(
        families="tagStandard41h12",
        nthreads=1,
        quad_decimate=1.0,
        quad_sigma=0.0,
        refine_edges=1,
        decode_sharpening=0.25,
        debug=0,
    )


def getTagDetectionResults(img, at_detector, camera_params, draw=False):
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    estimate_pose = True
    # validate the camera_params
    for param in camera_params:
        if not isinstance(param, (int, float)) or param <= 0:
            print(
                f"Invalid camera parameter value: {param}. All values must be positive numbers."
            )
            estimate_pose = False
    results = at_detector.detect(
        gray,
        estimate_tag_pose=estimate_pose,
        camera_params=camera_params,
        tag_size=TAG_SIZE,
    )
    if draw:
        # print(results)
        for detection in results:
            img = drawTagInfo(img, detection)
        # Display the resulting frame
        plt.imshow(cv2.cvtColor(img, cv2.COLOR_BGR2RGB))
        plt.show()
    return results


def getTagPoseToWorld(tag_results, T_w_cam):
    T_cam_tag = np.hstack(
        (
            BASIS_CHANGE.T @ tag_results.pose_R @ BASIS_CHANGE @ R_ALIGN,
            BASIS_CHANGE @ tag_results.pose_t,
        )
    )
    T_cam_tag = np.vstack((T_cam_tag, [0, 0, 0, 1]))
    return T_w_cam @ T_cam_tag, T_cam_tag


def getTagPoseToWorldFromImage(img, at_detector, camera_params, T_w_cam):
    results = getTagDetectionResults(img, at_detector, camera_params)
    if len(results) == 0 or results[0].pose_R is None:
        return None
    T_w_tag, _ = getTagPoseToWorld(results[0], T_w_cam)
    return T_w_tag
