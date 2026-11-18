namespace FocusAgent.Core.Auth;

/// <summary>The school account the agent signed in with (#525).</summary>
/// <param name="Username">
/// The sign-in name, an address such as <c>firstname.lastname@student.school.be</c>:
/// what the student signs in to Edge with, and what the pause names when no Edge
/// profile is signed in with it.
/// </param>
/// <param name="ObjectId">The account's Entra object ID, when known.</param>
public sealed record SchoolAccount(string Username, string? ObjectId);

/// <summary>
/// Holds the account the agent is signed in with, for what needs to know whose
/// laptop this is beyond the hub connection: during a session only the Edge
/// profile signed in with this account may be used (#525). Set on every
/// successful sign-in; null until the first one.
/// </summary>
public sealed class SignedInAccount
{
    private SchoolAccount? _current;

    public SchoolAccount? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}
