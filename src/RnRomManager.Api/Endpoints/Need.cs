namespace RnRomManager.Api.Endpoints;

/// <summary>All: every file is needed. OneOf: at least one. Optional: none (play falls back or skips a feature).</summary>
public enum Need { All, OneOf, Optional }
