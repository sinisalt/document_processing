using System.Xml;

namespace DocProc.Extraction.EInvoice.Internal;

/// <summary>
/// Forwards every call to the underlying reader and stops with an <see cref="EInvoiceParseException"/> at the first
/// element nested deeper than the limit, so a crafted document with hundreds of thousands of nested elements costs
/// only the first few hundred of them, whether the caller is the detector's forward scan or an XDocument load.
/// </summary>
internal sealed class DepthLimitedXmlReader(XmlReader inner, int maxElementDepth) : XmlReader
{
    public override int AttributeCount => inner.AttributeCount;

    public override string BaseURI => inner.BaseURI;

    public override bool CanReadBinaryContent => inner.CanReadBinaryContent;

    public override bool CanReadValueChunk => inner.CanReadValueChunk;

    public override bool CanResolveEntity => inner.CanResolveEntity;

    public override int Depth => inner.Depth;

    public override bool EOF => inner.EOF;

    public override bool HasAttributes => inner.HasAttributes;

    public override bool HasValue => inner.HasValue;

    public override bool IsDefault => inner.IsDefault;

    public override bool IsEmptyElement => inner.IsEmptyElement;

    public override string LocalName => inner.LocalName;

    public override string Name => inner.Name;

    public override string NamespaceURI => inner.NamespaceURI;

    public override XmlNameTable NameTable => inner.NameTable;

    public override XmlNodeType NodeType => inner.NodeType;

    public override string Prefix => inner.Prefix;

    public override char QuoteChar => inner.QuoteChar;

    public override ReadState ReadState => inner.ReadState;

    public override XmlReaderSettings? Settings => inner.Settings;

    public override string Value => inner.Value;

    public override string XmlLang => inner.XmlLang;

    public override XmlSpace XmlSpace => inner.XmlSpace;

    public override string GetAttribute(int i) => inner.GetAttribute(i);

    public override string? GetAttribute(string name) => inner.GetAttribute(name);

    public override string? GetAttribute(string name, string? namespaceURI) => inner.GetAttribute(name, namespaceURI);

    public override string? LookupNamespace(string prefix) => inner.LookupNamespace(prefix);

    public override void MoveToAttribute(int i) => inner.MoveToAttribute(i);

    public override bool MoveToAttribute(string name) => inner.MoveToAttribute(name);

    public override bool MoveToAttribute(string name, string? ns) => inner.MoveToAttribute(name, ns);

    public override bool MoveToElement() => inner.MoveToElement();

    public override bool MoveToFirstAttribute() => inner.MoveToFirstAttribute();

    public override bool MoveToNextAttribute() => inner.MoveToNextAttribute();

    public override bool Read()
    {
        if (!inner.Read())
        {
            return false;
        }

        if (inner.NodeType == XmlNodeType.Element && inner.Depth > maxElementDepth)
        {
            throw new EInvoiceParseException(
                $"The e-invoice XML nests elements deeper than the limit of {maxElementDepth} ('{inner.LocalName}' at depth {inner.Depth}).");
        }

        return true;
    }

    public override bool ReadAttributeValue() => inner.ReadAttributeValue();

    public override int ReadValueChunk(char[] buffer, int index, int count) => inner.ReadValueChunk(buffer, index, count);

    public override void ResolveEntity() => inner.ResolveEntity();

    public override void Close() => inner.Close();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
