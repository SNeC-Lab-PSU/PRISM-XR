using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class UserInfo : MonoBehaviour
{
    [SerializeField]
    private TextMeshPro _userNameText;

    public void UpdateUserName(string userName)
    {
        _userNameText.text = userName;
    }
}
