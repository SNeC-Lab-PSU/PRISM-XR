"""
This script is used to test the low-level socket server.
"""

import socket
import struct

HOST = "localhost"  # The server's hostname or IP address
PORT = 48102  # The port used by the server

with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
    s.connect((HOST, PORT))
    s.close()
