namespace RnRomManager.Api.Http;

/// <summary>Thrown by the connect callback when a host resolves to an address the policy refuses (U3).</summary>
public sealed class BlockedAddressException(string host) : Exception($"{host} resolves to a non-public address.");
