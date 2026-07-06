using System;
using System.Collections.Generic;

namespace InsideOS.Services.Narration;

/// <summary>
/// How strongly an interpretation is supported. The scale is honest by
/// convention across the whole app: High only when the statement is directly
/// backed by measured evidence; Medium for "likely/probably" pattern matches;
/// Low for "possibly" — several explanations fit the same evidence.
/// </summary>
public enum NarrationConfidence
{
    Low,
    Medium,
    High,
}

/// <summary>What part of the system an activity is about (drives icons/tints).</summary>
public enum ActivityCategory
{
    System,
    Cpu,
    Memory,
    Disk,
    Network,
    Application,
    Battery,
}

/// <summary>Tone of a moment narration — lets the UI style idle vs. active states.</summary>
public enum ActivityTone
{
    Observing,   // still collecting data
    Idle,        // nothing notable happening
    Activity,    // the process is doing something
    Terminated,  // the process is gone
}

/// <summary>
/// How directly a supporting fact was obtained. Measured = read directly
/// from the kernel (exact counters, process existence, own-process CPU
/// deltas). Observed = derived from sampled or smoothed readings (ps
/// averages, deltas across a window). Interpretations themselves are never
/// evidence — hedged wording ("likely", "possibly") plus
/// <see cref="NarrationConfidence"/> carry that distinction.
/// </summary>
public enum EvidenceQuality
{
    Measured,
    Observed,
}

/// <summary>One supporting fact behind an interpretation. Facts only — no guesses.</summary>
public sealed record Evidence(string Fact, EvidenceQuality Quality);

/// <summary>
/// A reusable interpretation of some activity: one hedged summary, the
/// confidence it deserves, and the measured/observed facts supporting it.
/// Pages decide how much of this to show; the reasoning is shared.
/// </summary>
public sealed record Interpretation(
    string Summary,
    NarrationConfidence Confidence,
    IReadOnlyList<Evidence> Evidence);

/// <summary>
/// A system-level narrated activity (the model behind Insight cards). The Id
/// is stable for a given rule + subject so UIs can update cards in place
/// without flicker.
/// </summary>
public sealed record NarratedActivity(
    string Id,
    ActivityCategory Category,
    string IconKey, // StreamGeometry resource name
    string Title,
    string Detail,
    NarrationConfidence Confidence,
    DateTime Timestamp);

/// <summary>
/// The single-moment interpretation of one process, before any presentation
/// smoothing. RuleId is stable per rule so consumers can apply hysteresis.
/// </summary>
public sealed record MomentNarration(
    string RuleId,
    string Text,
    ActivityTone Tone,
    NarrationConfidence Confidence,
    IReadOnlyList<Evidence> Evidence);
