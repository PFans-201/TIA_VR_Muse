using System.Collections.Generic;
using UnityEngine;

/// Keeps every puzzle piece inside the room's interior volume — even while a piece is
/// being held, when the wall colliders alone wouldn't stop the player carrying it out.
///
/// The walls/floor/ceiling are real colliders, so released pieces already bounce off
/// them; this is the backstop for grabbed pieces (the hand can otherwise drag a piece
/// straight through a wall). Runs in LateUpdate so the clamp wins over the grab
/// interactable's own transform write that frame.
///
/// The scene builder adds ONE of these to the puzzle room and sets the interior bounds.
public class RoomPieceContainer : MonoBehaviour
{
    [Tooltip("Centre of the allowed interior volume (world space).")]
    public Vector3 interiorCenter = new Vector3(0f, 1.4f, 0f);

    [Tooltip("Full size of the allowed interior volume (world space). Pieces are clamped inside this box.")]
    public Vector3 interiorSize = new Vector3(7.4f, 2.7f, 7.4f);

    [Tooltip("How often (seconds) to rescan the scene for new/active pieces. 0 = every frame.")]
    public float rescanInterval = 1f;

    private readonly List<Transform> _pieces = new();
    private float _rescanTimer;

    private void Start() => Rescan();

    private void LateUpdate()
    {
        _rescanTimer += Time.deltaTime;
        if (rescanInterval <= 0f || _rescanTimer >= rescanInterval)
        {
            _rescanTimer = 0f;
            Rescan();
        }

        Vector3 min = interiorCenter - interiorSize * 0.5f;
        Vector3 max = interiorCenter + interiorSize * 0.5f;

        for (int i = _pieces.Count - 1; i >= 0; i--)
        {
            var t = _pieces[i];
            if (t == null) { _pieces.RemoveAt(i); continue; }

            Vector3 p = t.position;
            Vector3 c = new Vector3(
                Mathf.Clamp(p.x, min.x, max.x),
                Mathf.Clamp(p.y, min.y, max.y),
                Mathf.Clamp(p.z, min.z, max.z));

            if (c != p)
            {
                t.position = c;
                // Kill outward velocity so a released piece doesn't keep fighting the wall.
                if (t.TryGetComponent<Rigidbody>(out var rb))
                {
                    Vector3 v = rb.linearVelocity;
                    if (c.x != p.x) v.x = 0f;
                    if (c.y != p.y) v.y = 0f;
                    if (c.z != p.z) v.z = 0f;
                    rb.linearVelocity = v;
                }
            }
        }
    }

    private void Rescan()
    {
        _pieces.Clear();
        foreach (var piece in FindObjectsByType<PuzzlePiece>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            _pieces.Add(piece.transform);
    }
}
