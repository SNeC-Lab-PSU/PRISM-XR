# PRISM-XR: Empowering Privacy-Aware XR Collaboration with Multimodal Large Language Models

Welcome to the repository for **PRISM-XR: Empowering Privacy-Aware XR Collaboration with Multimodal Large Language Models**. This repository contains the implementation for the methods and experiments described in our paper.

We are excited to open source our implementation for **academic and non-commercial use**, enabling the community to explore and extend our work.

**Note**: This implementation is currently in a **preliminary stage** and intended for **prototype use only**. Future updates may expand its functionality and robustness.

---

## Prerequisites
- FFmpeg
    - Required before running the Whisper model.
    - Ensure FFmpeg is added to the system PATH.
- Python 3.10
    - Both Windows and Linux system could run the server script with proper configuration, while the configuration in Linux system is easier with less unexpected issues.
- Unity 2022.3.58f1
    - **Porcupine Unity Package** is required to be imported into the project for the voice wake up command. Download the package by following the instructions [from the official documentation](https://github.com/Picovoice/porcupine/tree/a5a57062b2fa8b766912b787c43c3afd5efa9a4d/binding/unity).
- Meta Quest 3

---

## Preparation

- Make sure all [prerequisites](#prerequisites) are met.
- Clone the repository.
- Create `.env` file in the root directory of the project with the following content:
    ```
    OPENAI_API_KEY=YOUR_OPENAI_API_KEY
    ```
- Obtain an access key from [Porcupine](https://console.picovoice.ai/) and enter this key in the `PORCUPINE_ACCESS_KEY` variable in **Utils** script in Unity project for Quest devices.
- Create a virtual environment in Python 3.10 (e.g., [using Anaconda](https://docs.anaconda.com/working-with-conda/environments/#creating-an-environment)) and install the required packages.
    ```bash
    pip install -r requirements.txt
    ```
- Run `python openAIWrapper.py` in the virtual environment to test the OpenAI API connection.
- Download Unity Hub and install Unity 2022.3.58f1 in Unity Hub with Android Build Support module.

---

## Steps to Run the Project

1. Run the server by running `python pyServer.py` in the root directory of the project, ensure the server is running before running the Unity application.
    - Run `python echoclient.py` in the virtual environment to test the server.
2. Open the folder `LLM-Quest` in Unity Hub with Unity 2022.3.58f1. At the first time running the project, you may find a pop-up window suggesting compilation errors and entering safe mode. Click **Enter Safe Mode**. 
3. Install Porcupine Unity Package in the Package Manager by following the instructions [from the official documentation](https://github.com/Picovoice/porcupine/tree/a5a57062b2fa8b766912b787c43c3afd5efa9a4d/binding/unity). Make sure your access key has been filled in the `Utils.cs` file.
4. Unity Editor will automatically exit safe mode after the installation, you may find a pop-up window asking to restart Unity due to changes of OVR Plugin. Click **Restart Editor** to complete the update.
5. Navigate to `Assets/Scenes` folder, open the `MainWorld` scene.
6. Change the Server URL with proper IP and PORT of **WebSocketClient** attached to **ControlCenter** GameObject in Unity Inspector window. **NOTE**: The default Server IP is `localhost`, which should be changed to the IP address of the computer running the server code, e.g., `192.168.1.1`, the Server Port and Server Port Low Level should be changed to port numbers that are allowed under your group policy.
7. Either press the Play button to test the scene in Editor or connect a Meta Quest device to deploy the application.

---

## Citation
If you use this implementation, methodology, or any part of this repository in your research, please cite our paper:

```bibtex
@article{chen2026prism-xr,
  title={PRISM-XR: Empowering Privacy-Aware XR Collaboration with Multimodal Large Language Models},
  author={Chen, Jiangong and Zhu, Mingyu and Li, Bin},
  journal={2026 IEEE Conference Virtual Reality and 3D User Interfaces (VR)},
  year={2026},
  publisher={IEEE}
}
```

---

## License
This repository is licensed under the **Creative Commons Attribution-NonCommercial 4.0 International (CC BY-NC 4.0) License**. This means:

- You are free to **use, modify, and share** the code and assets **for academic and non-commercial purposes**.
- **Commercial use is prohibited** without explicit permission.

For commercial licensing or inquiries, please **contact us**.

For more details, see the [LICENSE](./LICENSE) file or visit the [official CC BY-NC 4.0 page](https://creativecommons.org/licenses/by-nc/4.0/).

---

## Contributions
We welcome academic collaborations and constructive feedback. Feel free to open issues or submit pull requests to enhance this repository post-release. Let’s work together to advance the fields of XR and generative AI!

---

## Contact
For any questions, clarifications, or collaboration opportunities, please reach out via:
- Email: [jiangong@psu.edu] or [mintrrey@psu.edu]
- GitHub: [JiangongChen](https://github.com/JiangongChen) or [MingyuZhu](https://github.com/mintrrey)
- Institution: The Pennsylvania State University

Thank you for your interest in PRISM-XR! Stay tuned for future updates and enhancements.
