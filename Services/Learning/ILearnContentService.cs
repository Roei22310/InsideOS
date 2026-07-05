using InsideOS.Services.ActionFlow;

namespace InsideOS.Services.Learning;

/// <summary>
/// Provides all educational content shown in Learn Mode. The UI never
/// hardcodes text — it always asks this service, so content can grow
/// (more topics, more languages, more knowledge levels) or be enhanced by
/// an AI generator later without changing any UI code.
/// </summary>
public interface ILearnContentService
{
    /// <summary>Static concept explanation ("what does this component do?").</summary>
    LearnContent GetContent(LearnTopicId topic, KnowledgeLevel level = KnowledgeLevel.Beginner, string language = "en");

    /// <summary>Connects the live metrics to the concept ("why is this happening?").</summary>
    string DescribeWhy(LearnTopicId topic, ProcessFlowSnapshot snapshot);

    /// <summary>Simple reassurance or guidance ("should I worry?").</summary>
    string DescribeWorry(LearnTopicId topic, ProcessFlowSnapshot snapshot);
}
