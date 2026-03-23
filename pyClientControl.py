"""
This script is used for global control
For example, clear user message history or scene objects on the server side
"""

import asyncio
import json
from websockets.asyncio.client import connect

DELIMITER = "<END_HEADER>"


async def send_data(websocket, data_type: str, data, extra_info=None):
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


async def hello():
    async with connect("ws://localhost:48101") as websocket:
        await send_data(websocket, "control", b"", "clearMsg")


if __name__ == "__main__":

    asyncio.run(hello())
