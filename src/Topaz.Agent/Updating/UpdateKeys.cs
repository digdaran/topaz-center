namespace Topaz.Agent.Updating;

/// <summary>
/// Public half of the key CI signs agent packages with (GitHub secret AGENT_SIGNING_KEY).
/// The private key never reaches the central server, so a compromised server cannot push code to stations.
/// Rotating it means one manual reinstall of the agent with the new key.
/// </summary>
public static class UpdateKeys
{
    public const string ReleasePublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE/JardYWF6nAyhP1UP3Oka9BzoY6w
        qvJL4qq93NFrwp+q5RgzLKlw20RoYkNT+IlE9KzSr65wYWXH4VenecJ1ig==
        -----END PUBLIC KEY-----
        """;
}
