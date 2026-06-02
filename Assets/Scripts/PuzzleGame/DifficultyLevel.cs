using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Shared enum for difficulty levels — used by DifficultyUI, PuzzleManager, PuzzlePiece.
/// Keep this in its own file so all scripts can reference it without circular dependencies.
/// </summary>
public enum DifficultyLevel
{
    Easy,
    Medium,
    Hard
}
