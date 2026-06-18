using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Databases;

public sealed class MongoToolMissingException : InvalidOperationException
{
    public PackageType NeededTool { get; }
    public string ToolLabel { get; }

    public MongoToolMissingException(PackageType neededTool, string toolLabel, string message)
        : base(message)
    {
        NeededTool = neededTool;
        ToolLabel = toolLabel;
    }
}
