"""
Using State of the Art Object Detection Models to detect objects in images
This process is executed on the edge server, preventing the need to send the whole image to the cloud for privacy reasons
"""

from ultralytics import YOLO

# Load a model
model = YOLO("yolo11x.pt")


def detect_img(img_path):
    # Perform object detection on an image
    results = model(img_path)
    # Access the first result (since only processing a single image)
    result = results[0]

    description_string = ""
    # Iterate over detected boxes
    for box in result.boxes:
        # Get bounding box coordinates in xyxy format
        xyxy = (
            box.xyxy[0].cpu().numpy()
        )  # [x1, y1, x2, y2], which are coordinates of top-left corner and bottom-right corner
        xyxy_str = ", ".join(
            f"{value:.2f}" for value in xyxy
        )  # Convert to a formatted string

        # Get the class ID and confidence score
        class_id = int(box.cls[0])
        confidence = box.conf[0].item()

        # Get the class name
        class_name = result.names[class_id]

        # Get the center coordinates of the bounding box
        center_x = (xyxy[0] + xyxy[2]) / 2
        center_y = (xyxy[1] + xyxy[3]) / 2

        # Add the object description to the string
        description_string += f"{class_name}, center ({center_x:.2f}, {center_y:.2f}), box ({xyxy_str}), confidence {confidence:.2f}\n"
    print(f"Detected objects:\n{description_string}")
    return description_string


if __name__ == "__main__":
    # Test the object detection function
    img_path = "temp/testRec.jpg"
    results = model(img_path)
    results[0].show()
    detect_img(img_path)
