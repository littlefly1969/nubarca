namespace NubArca.Api.Domain;

/// <summary>
/// WHICH FACE IN THE SELFIE the party is going to search for, as a pure
/// function.
///
/// <para>A guest points a phone at themselves in a room full of people, so a
/// selfie with two faces in it is the normal case rather than the exception.
/// What the product did about that was take the largest box and search for it,
/// which is right most of the time and, the rest of the time, silently hands
/// somebody else's photographs to the wrong person. There is no "slightly
/// wrong" here: the guest gets a stranger's evening, or the stranger's face
/// gets searched for by somebody who is not them.</para>
///
/// <para><b>So the rule is stated, deterministic, and allowed to refuse.</b>
/// One face is that face. Several faces produce one only when there is a
/// DOMINANT one — measurably bigger than everything else, and near the middle
/// of the frame, which is where somebody holding the phone actually is.
/// Otherwise the answer is <see cref="PartyFaceChoice.Ambiguous"/> and the
/// guest is asked for a selfie with only themselves in it, which costs them
/// four seconds and costs nobody their privacy.</para>
///
/// <para>It lives in the domain, and not in the browser, for a reason the
/// product cares about: the face the scanner SHOWS must be the face the search
/// USES. One implementation, run once, on the server that does the matching —
/// a second opinion computed in JavaScript would eventually disagree with it,
/// and the frame would then be a picture of a decision nobody made.</para>
/// </summary>
public static class PartyFaceSelection
{
    /// <summary>
    /// How much bigger the leading face must be than the next one to count as
    /// dominant, measured by AREA.
    ///
    /// <para>1.6 is roughly "noticeably closer to the camera". Two people
    /// standing side by side at arm's length differ by a few per cent and are
    /// correctly refused; the person holding the phone is typically two to four
    /// times the area of anybody behind them.</para>
    /// </summary>
    public const double DominantAreaRatio = 1.6;

    /// <summary>
    /// How far from the middle the dominant face's centre may be, as a fraction
    /// of the image, on each axis.
    ///
    /// <para>Wider horizontally than vertically is deliberate: an arm holds a
    /// phone off to one side far more often than a head sits at the top or
    /// bottom of a selfie.</para>
    /// </summary>
    public const double CentreToleranceX = 0.28;
    public const double CentreToleranceY = 0.32;

    /// <summary>The outcome. Three cases, and none of them is a guess.</summary>
    public sealed record Choice(DetectedFaceBox? Face, bool Ambiguous)
    {
        public static readonly Choice None = new(null, false);
        public static readonly Choice TooMany = new(null, true);
        public static Choice Of(DetectedFaceBox face) => new(face, false);

        /// <summary>No face at all: nothing to search for, and nothing to ask.</summary>
        public bool Empty => Face is null && !Ambiguous;
    }

    /// <summary>
    /// A detected face, reduced to the geometry this decision needs.
    ///
    /// <para>Fractions of the image on both axes, exactly as the detector
    /// produces them. Landmarks, confidence and everything else the backend
    /// carries are deliberately absent: a rule that could read them would
    /// eventually read them, and this has to stay explainable in one
    /// paragraph.</para>
    /// </summary>
    public readonly record struct DetectedFaceBox(double X, double Y, double Width, double Height)
    {
        public double Area => Width <= 0 || Height <= 0 ? 0 : Width * Height;
        public double CentreX => X + (Width / 2);
        public double CentreY => Y + (Height / 2);
    }

    /// <summary>
    /// The face to search for, or a refusal.
    ///
    /// <para>Degenerate boxes — zero or negative width or height, which a
    /// backend should not produce and one once did — are dropped before the
    /// rule runs, so they can neither win nor make a real face look ambiguous.</para>
    /// </summary>
    public static Choice Choose(IReadOnlyList<DetectedFaceBox> faces)
    {
        var real = faces.Where(f => f.Area > 0).OrderByDescending(f => f.Area).ToList();
        if (real.Count == 0)
        {
            return Choice.None;
        }

        if (real.Count == 1)
        {
            return Choice.Of(real[0]);
        }

        var leader = real[0];
        var runnerUp = real[1];

        // BIGGER THAN EVERYTHING ELSE, and it is the runner-up that decides:
        // comparing against the average would let one large face and four tiny
        // ones look dominant when two large faces are the actual situation.
        if (leader.Area < runnerUp.Area * DominantAreaRatio)
        {
            return Choice.TooMany;
        }

        // AND WHERE THE PERSON HOLDING THE PHONE IS. A big face at the very
        // edge of the frame is usually somebody walking past close to the
        // camera, which is exactly the case where guessing is worst.
        var offCentre = Math.Abs(leader.CentreX - 0.5) > CentreToleranceX
            || Math.Abs(leader.CentreY - 0.5) > CentreToleranceY;

        return offCentre ? Choice.TooMany : Choice.Of(leader);
    }
}
