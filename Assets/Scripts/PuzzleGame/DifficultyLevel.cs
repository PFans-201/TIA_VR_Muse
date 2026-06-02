/// Shared enums — used across DifficultyUI, PuzzleManager, PieceHintSystem.
/// Keep in one file so all scripts can reference without circular dependencies.

public enum DifficultyLevel
{
    Easy,
    Medium,
    Hard
}

public enum PuzzleType
{
    Snowman,   // Simple: 3 / 5 / 7 pieces
    Robot      // Complex: 5 / 8 / 12 pieces
}

public enum ProgressionMode
{
    FreeChoice,  // All difficulties always available (useful for testing / free play)
    Sequential   // Easy → Medium → Hard; each unlocks only after the previous is completed
}
