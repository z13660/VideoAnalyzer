namespace VideoAnalyzer.Models;

/// <summary>
/// One contiguous stretch of frames that carries QP or motion data, in seconds.
///
/// The graph shading is drawn from a list of these rather than from a single span: analysing
/// several separate ranges leaves gaps between them, and a span would paint the gaps as if they
/// had been measured too.
/// </summary>
public readonly record struct AnalysedRun(double From, double To);
