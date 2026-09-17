namespace NubArca.Api.Audit;

/// <summary>
/// WHO did the thing — and there are now three answers, not two.
///
/// <para>Until Party Crew, every audited action was either a signed-in user or
/// nobody, and <c>Guid? userId</c> said which. A collaborator is neither. They
/// are not a <c>User</c> and never become one, so writing the owner's id would
/// make a co-organizer's revoke read, in every existing query and export, as
/// something the HOST did — and writing null would put it in the same bucket as
/// an anonymous guest. Both are wrong in the one direction an audit trail must
/// never be wrong: the actor.</para>
///
/// <para>So the actor is a type, and the two ids are separate columns. A row
/// carries at most one of them, and "which kind of actor" is answerable in SQL
/// rather than by parsing metadata.</para>
///
/// <para>Every existing call site keeps working unchanged: a <c>Guid</c> or a
/// <c>Guid?</c> converts implicitly and still means a user.</para>
/// </summary>
public readonly record struct AuditActor
{
    private AuditActor(Guid? userId, Guid? partyCollaboratorId)
    {
        UserId = userId;
        PartyCollaboratorId = partyCollaboratorId;
    }

    /// <summary>The signed-in user, when that is who acted.</summary>
    public Guid? UserId { get; }

    /// <summary>
    /// The party collaborator, when a Party Crew device acted. Never set
    /// together with <see cref="UserId"/>: an action has one actor.
    /// </summary>
    public Guid? PartyCollaboratorId { get; }

    /// <summary>Nobody identified — a guest, a public surface, the system.</summary>
    public static AuditActor Anonymous => new(null, null);

    public static AuditActor User(Guid userId) => new(userId, null);

    /// <summary>
    /// A Party Crew collaborator. The id, never the name and never the address:
    /// an audit trail holds no personal data.
    /// </summary>
    public static AuditActor Crew(Guid partyCollaboratorId) => new(null, partyCollaboratorId);

    /// <summary>
    /// The compatibility seam. One operator rather than two — a bare
    /// <c>Guid</c> reaches it through the standard conversion to <c>Guid?</c>,
    /// and a literal <c>null</c> is unambiguous.
    /// </summary>
    public static implicit operator AuditActor(Guid? userId) =>
        userId is null ? Anonymous : User(userId.Value);
}
