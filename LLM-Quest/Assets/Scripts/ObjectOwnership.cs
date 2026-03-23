using UnityEngine;
using Meta.XR.MultiplayerBlocks.Shared;

/**
 * This script manages the ownership of interactable objects among users.
 * This script should be used together with the scripts when making objects grabbable.
 */
public class ObjectOwnership : MonoBehaviour, ITransferOwnership
{
    [SerializeField]
    private bool _ownedByCurrentUser = false;
    private SyncManager _syncManager;
    private GameObject _targetObj;

    public void TransferOwnershipToLocalPlayer()
    {
        RequestOwnership();
    }

    public bool HasOwnership()
    {
        return GetOwnership();
    }

    public void AssignSyncManager(SyncManager syncManager)
    {
        _syncManager = syncManager;
    }

    public void RequestOwnership()
    {
        _syncManager.AddOwnedObject(_targetObj, "transferOwner");
    }

    public void SetOwnerShip(bool ownedByCurrentUser)
    {
        _ownedByCurrentUser = ownedByCurrentUser;
    }

    public bool GetOwnership()
    {
        return _ownedByCurrentUser;
    }

    public void SetTargetObj(GameObject targetObj)
    {
        _targetObj = targetObj;
    }
}
