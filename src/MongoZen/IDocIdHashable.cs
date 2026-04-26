namespace MongoZen;

public interface IDocIdHashable
{
    // Write your identity bytes into the span, return bytes written.
    // Implement this on your POCO key and you never touch BSON serialization.
    int WriteIdBytes(Span<byte> destination);
}