namespace Scout.Knowledge;

/// <summary>A raw chunk before embedding.</summary>
public readonly record struct Chunk(string Symbol, string Kind, int StartLine, int EndLine, string Text);
