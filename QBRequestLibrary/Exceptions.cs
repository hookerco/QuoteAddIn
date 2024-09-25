using System;

public class NoValueException : Exception
{
    public NoValueException(string message = "No value is set. Use Set(value).") : base(message) { }
}

public class InvalidResponseException : Exception
{
    public InvalidResponseException(string message = "Response cannot be parsed") : base(message) { }
}

public class QBRequestLibraryRuntimeError : Exception
{
    public QBRequestLibraryRuntimeError(string message) : base(message) { }
}