using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace RnRomManager.Api.Tests;

/// <summary>Stub-mode key ring: keys live as long as the host.</summary>
internal sealed class InMemoryXmlRepository : IXmlRepository
{
    private readonly List<XElement> _elements = [];

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        lock (_elements)
            return _elements.Select(e => new XElement(e)).ToList();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        lock (_elements)
            _elements.Add(new XElement(element));
    }
}
