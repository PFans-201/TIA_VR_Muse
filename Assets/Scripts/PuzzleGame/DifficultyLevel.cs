/// Shared enums — used across DifficultyUI, PuzzleManager, PieceHintSystem.
/// Keep in one file so all scripts can reference without circular dependencies.

public enum DifficultyLevel
{
    Easy,
    Medium,
    Hard
}

/// How the pieces are positioned when a puzzle starts.
public enum SpawnMode
{
    NearSolved,       // Easy   — small offset from each piece's solved slot
    OffsetFromSolved, // Medium — a larger offset, still relative to the solved slot
    CeilingDrop       // Hard   — random x/z at ceiling height; pieces fall and settle
}

public enum ProgressionMode
{
    FreeChoice,  // All difficulties always available (useful for testing / free play)
    Sequential   // Easy → Medium → Hard; each unlocks only after the previous is completed
}
