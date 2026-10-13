namespace Zyggy.Core.Configuration;

/// <summary>Why <c>node.json</c> was refused (founding spec §10). Closed.</summary>
public enum NodeConfigurationError
{
    /// <summary>The file does not exist at the resolved path.</summary>
    FileMissing,

    /// <summary>The file is not a JSON object.</summary>
    Malformed,

    /// <summary>A required key is absent (<c>tenant</c> above all: there is no default tenant).</summary>
    MissingField,

    /// <summary>A key has an invalid value, or a key names a secret.</summary>
    InvalidField,
}
