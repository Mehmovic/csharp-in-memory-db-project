namespace RhinoDB.SchemaContracts;

public sealed class SchemaContractDescriptorException(string message, Exception? innerException = null) : Exception(message, innerException);
