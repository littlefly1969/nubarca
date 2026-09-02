using NubArca.Api.Domain.Print;

namespace NubArca.Api.Print;

public static class PrintJobStateMachine
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Allowed =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [PrintJobStates.Requested] = Set(PrintJobStates.Rendering, PrintJobStates.Cancelled),
            [PrintJobStates.Rendering] = Set(PrintJobStates.Ready, PrintJobStates.Failed, PrintJobStates.Cancelled),
            [PrintJobStates.Ready] = Set(PrintJobStates.Claimed, PrintJobStates.Cancelled),
            [PrintJobStates.Claimed] = Set(PrintJobStates.Submitting, PrintJobStates.Ready,
                PrintJobStates.Failed, PrintJobStates.Cancelled, PrintJobStates.DeliveryUnknown),
            [PrintJobStates.Submitting] = Set(PrintJobStates.Submitted, PrintJobStates.Failed,
                PrintJobStates.DeliveryUnknown),
            [PrintJobStates.Submitted] = Set(PrintJobStates.Completed, PrintJobStates.Failed,
                PrintJobStates.DeliveryUnknown),
            [PrintJobStates.Completed] = Set(),
            [PrintJobStates.Failed] = Set(PrintJobStates.Ready),
            [PrintJobStates.Cancelled] = Set(),
            [PrintJobStates.DeliveryUnknown] = Set(),
        };

    public static bool CanTransition(string from, string to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static void EnsureTransition(string from, string to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"Invalid print-job transition: {from} -> {to}.");
        }
    }

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);
}
