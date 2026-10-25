// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Samples;

/// <summary>The reference corpus and labels consumed by sample B1.</summary>
internal static class ComprehensiveRagExampleData
{
    // v2 makes the city scope explicit. doc2/3/4 support every expected fact; doc8 does not locate Versailles in Paris.
    // This corrects the original e5cfc6b2 fixture; it does not preserve comparability with its unversioned scores.
    public const string DatasetVersion = "b1-capital-landmarks-v2";
    public const string Query = "What is the capital of France and what famous landmarks are in that city?";
    public const string GroundTruth = "Paris is the capital of France. Famous landmarks include the Eiffel Tower and the Louvre Museum.";

    public static IReadOnlyList<(string Id, string Text)> KnowledgeBase { get; } =
        Array.AsReadOnly<(string Id, string Text)>(
        [
            ("doc1", "France is a country in Western Europe. It is known for its rich history, art, and culture. The official language is French, and the currency is the Euro."),
            ("doc2", "Paris is the capital and largest city of France. It has a population of over 2 million in the city proper and over 12 million in the metropolitan area."),
            ("doc3", "The Eiffel Tower is a wrought-iron lattice tower in Paris, France. It was constructed from 1887 to 1889 as the entrance arch for the 1889 World's Fair."),
            ("doc4", "The Louvre Museum in Paris is the world's largest art museum. It houses over 35,000 works of art including the Mona Lisa and the Venus de Milo."),
            ("doc5", "French cuisine is renowned worldwide. Popular dishes include croissants, baguettes, coq au vin, and crème brûlée. France is also famous for its wines and cheeses."),
            ("doc6", "The French Revolution began in 1789 and fundamentally changed France. It led to the end of the monarchy and the rise of democratic ideals."),
            ("doc7", "Mont Blanc is the highest peak in the Alps and Western Europe at 4,808 meters. It is located on the border between France and Italy."),
            ("doc8", "The Palace of Versailles was the principal royal residence of France from 1682 until the French Revolution. It is known for the Hall of Mirrors and its vast gardens.")
        ]);

    public static IReadOnlyList<string> RelevantDocumentIds { get; } =
        Array.AsReadOnly<string>(["doc2", "doc3", "doc4"]);
}
