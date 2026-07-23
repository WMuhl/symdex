using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Symdex.Core;

namespace Symdex.Roslyn;

internal static class SymbolFormatting
{
    private static readonly SymbolDisplayFormat FqNameFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters
                       | SymbolDisplayMemberOptions.IncludeType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private static readonly SymbolDisplayFormat SignatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType
                       | SymbolDisplayMemberOptions.IncludeModifiers | SymbolDisplayMemberOptions.IncludeAccessibility,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName
                          | SymbolDisplayParameterOptions.IncludeDefaultValue,
        kindOptions: SymbolDisplayKindOptions.IncludeMemberKeyword | SymbolDisplayKindOptions.IncludeTypeKeyword,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static string FqName(ISymbol symbol) => symbol.ToDisplayString(FqNameFormat);

    public static string Signature(ISymbol symbol) => symbol.ToDisplayString(SignatureFormat);

    public static string Kind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => SymbolKinds.Interface,
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => SymbolKinds.Struct,
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => SymbolKinds.Enum,
        INamedTypeSymbol => SymbolKinds.Class,
        IMethodSymbol => SymbolKinds.Method,
        IPropertySymbol => SymbolKinds.Property,
        IEventSymbol => SymbolKinds.Property,
        IFieldSymbol => SymbolKinds.Field,
        _ => SymbolKinds.Type,
    };

    /// <summary>Plain-text content of the symbol's XML doc &lt;summary&gt; element, if any.</summary>
    public static string? Summary(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var doc = XDocument.Parse(xml);
            var summary = doc.Descendants("summary").FirstOrDefault();
            if (summary is null)
            {
                return null;
            }

            var text = string.Join(" ", summary.Value.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return text.Length == 0 ? null : text;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }
}
