namespace ManufacturingAI.Core;

public static class PromptDefaults
{
    // Default system-prompt instructions used when a tenant has not customized one.
    // The retrieved reference documents are appended automatically by the query
    // pipeline, so this text should NOT include a {context} placeholder.
    public const string SystemPrompt =
        """
        You are a manufacturing SOP and technical document assistant.
        Answer the user's question based only on the reference document excerpts provided below.
        If the excerpts do not contain enough information, clearly state that you do not know. Do not fabricate answers.
        Keep your response well-structured and concise.
        """;
}
