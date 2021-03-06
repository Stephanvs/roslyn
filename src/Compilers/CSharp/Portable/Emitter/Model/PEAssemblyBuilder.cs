﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Emit
{
    internal abstract class PEAssemblyBuilderBase : PEModuleBuilder, Cci.IAssemblyReference
    {
        private readonly SourceAssemblySymbol _sourceAssembly;
        private readonly ImmutableArray<NamedTypeSymbol> _additionalTypes;
        private ImmutableArray<Cci.IFileReference> _lazyFiles;

        private SynthesizedEmbeddedAttributeSymbol _lazyIsReadOnlyAttribute;
        private SynthesizedEmbeddedAttributeSymbol _lazyIsByRefLikeAttribute;
        private SynthesizedEmbeddedAttributeSymbol _lazyIsUnmanagedAttribute;
        private SynthesizedEmbeddedAttributeSymbol _lazyNullableAttribute;
        private ImmutableArray<NamedTypeSymbol> _lazyInjectedTypes;
        private bool _needsNonNullTypesAttribute;
        private bool _needsEmbeddedAttribute;

        /// <summary>
        /// The behavior of the C# command-line compiler is as follows:
        ///   1) If the /out switch is specified, then the explicit assembly name is used.
        ///   2) Otherwise,
        ///      a) if the assembly is executable, then the assembly name is derived from
        ///         the name of the file containing the entrypoint;
        ///      b) otherwise, the assembly name is derived from the name of the first input
        ///         file.
        /// 
        /// Since we don't know which method is the entrypoint until well after the
        /// SourceAssemblySymbol is created, in case 2a, its name will not reflect the
        /// name of the file containing the entrypoint.  We leave it to our caller to
        /// provide that name explicitly.
        /// </summary>
        /// <remarks>
        /// In cases 1 and 2b, we expect (metadataName == sourceAssembly.MetadataName).
        /// </remarks>
        private readonly string _metadataName;

        public PEAssemblyBuilderBase(
            SourceAssemblySymbol sourceAssembly,
            EmitOptions emitOptions,
            OutputKind outputKind,
            Cci.ModulePropertiesForSerialization serializationProperties,
            IEnumerable<ResourceDescription> manifestResources,
            ImmutableArray<NamedTypeSymbol> additionalTypes)
            : base((SourceModuleSymbol)sourceAssembly.Modules[0], emitOptions, outputKind, serializationProperties, manifestResources)
        {
            Debug.Assert((object)sourceAssembly != null);

            _sourceAssembly = sourceAssembly;
            _additionalTypes = additionalTypes.NullToEmpty();
            _metadataName = (emitOptions.OutputNameOverride == null) ? sourceAssembly.MetadataName : FileNameUtilities.ChangeExtension(emitOptions.OutputNameOverride, extension: null);

            AssemblyOrModuleSymbolToModuleRefMap.Add(sourceAssembly, this);
        }

        protected override void EnsureNonNullTypesAttributeExists()
        {
            Debug.Assert(!InjectedSymbolsAreFrozen);
            _needsNonNullTypesAttribute = true;
        }

        protected override void EnsureEmbeddedAttributeExists()
        {
            Debug.Assert(!InjectedSymbolsAreFrozen);
            _needsEmbeddedAttribute = true;
        }

        public override ISourceAssemblySymbolInternal SourceAssemblyOpt => _sourceAssembly;

        internal override ImmutableArray<NamedTypeSymbol> GetAdditionalTopLevelTypes(DiagnosticBag diagnostics)
        {
            return _additionalTypes;
        }

        internal override ImmutableArray<NamedTypeSymbol> GetEmbeddedTypes(DiagnosticBag diagnostics)
        {
            var builder = ArrayBuilder<NamedTypeSymbol>.GetInstance();

            CreateEmbeddedAttributesIfNeeded(diagnostics);

            if ((object)_lazyIsReadOnlyAttribute != null)
            {
                builder.Add(_lazyIsReadOnlyAttribute);
            }

            if ((object)_lazyIsUnmanagedAttribute != null)
            {
                builder.Add(_lazyIsUnmanagedAttribute);
            }

            if ((object)_lazyIsByRefLikeAttribute != null)
            {
                builder.Add(_lazyIsByRefLikeAttribute);
            }

            if ((object)_lazyNullableAttribute != null)
            {
                builder.Add(_lazyNullableAttribute);
            }

            return builder.ToImmutableAndFree();
        }

        public sealed override IEnumerable<Cci.IFileReference> GetFiles(EmitContext context)
        {
            if (_lazyFiles.IsDefault)
            {
                var builder = ArrayBuilder<Cci.IFileReference>.GetInstance();
                try
                {
                    var modules = _sourceAssembly.Modules;
                    for (int i = 1; i < modules.Length; i++)
                    {
                        builder.Add((Cci.IFileReference)Translate(modules[i], context.Diagnostics));
                    }

                    foreach (ResourceDescription resource in ManifestResources)
                    {
                        if (!resource.IsEmbedded)
                        {
                            builder.Add(resource);
                        }
                    }

                    // Dev12 compilers don't report ERR_CryptoHashFailed if there are no files to be hashed.
                    if (ImmutableInterlocked.InterlockedInitialize(ref _lazyFiles, builder.ToImmutable()) && _lazyFiles.Length > 0)
                    {
                        if (!CryptographicHashProvider.IsSupportedAlgorithm(_sourceAssembly.HashAlgorithm))
                        {
                            context.Diagnostics.Add(new CSDiagnostic(new CSDiagnosticInfo(ErrorCode.ERR_CryptoHashFailed), NoLocation.Singleton));
                        }
                    }
                }
                finally
                {
                    builder.Free();
                }
            }

            return _lazyFiles;
        }

        protected override void AddEmbeddedResourcesFromAddedModules(ArrayBuilder<Cci.ManagedResource> builder, DiagnosticBag diagnostics)
        {
            var modules = _sourceAssembly.Modules;
            int count = modules.Length;

            for (int i = 1; i < count; i++)
            {
                var file = (Cci.IFileReference)Translate(modules[i], diagnostics);

                try
                {
                    foreach (EmbeddedResource resource in ((Symbols.Metadata.PE.PEModuleSymbol)modules[i]).Module.GetEmbeddedResourcesOrThrow())
                    {
                        builder.Add(new Cci.ManagedResource(
                            resource.Name,
                            (resource.Attributes & ManifestResourceAttributes.Public) != 0,
                            null,
                            file,
                            resource.Offset));
                    }
                }
                catch (BadImageFormatException)
                {
                    diagnostics.Add(new CSDiagnosticInfo(ErrorCode.ERR_BindToBogus, modules[i]), NoLocation.Singleton);
                }
            }
        }

        public override string Name => _metadataName;
        public AssemblyIdentity Identity => _sourceAssembly.Identity;
        public Version AssemblyVersionPattern => _sourceAssembly.AssemblyVersionPattern;

        internal override SynthesizedAttributeData SynthesizeNullableAttribute(WellKnownMember member, ImmutableArray<TypedConstant> arguments)
        {
            if ((object)_lazyNullableAttribute != null)
            {
                var constructorIndex = (member == WellKnownMember.System_Runtime_CompilerServices_NullableAttribute__ctorTransformFlags) ? 1 : 0;
                return new SynthesizedAttributeData(
                    _lazyNullableAttribute.Constructors[constructorIndex],
                    arguments,
                    ImmutableArray<KeyValuePair<string, TypedConstant>>.Empty);
            }

            return base.SynthesizeNullableAttribute(member, arguments);
        }

        protected override SynthesizedAttributeData TrySynthesizeIsReadOnlyAttribute()
        {
            if ((object)_lazyIsReadOnlyAttribute != null)
            {
                return new SynthesizedAttributeData(
                    _lazyIsReadOnlyAttribute.Constructors[0],
                    ImmutableArray<TypedConstant>.Empty,
                    ImmutableArray<KeyValuePair<string, TypedConstant>>.Empty);
            }

            return base.TrySynthesizeIsReadOnlyAttribute();
        }

        protected override SynthesizedAttributeData TrySynthesizeIsUnmanagedAttribute()
        {
            if ((object)_lazyIsUnmanagedAttribute != null)
            {
                return new SynthesizedAttributeData(
                    _lazyIsUnmanagedAttribute.Constructors[0],
                    ImmutableArray<TypedConstant>.Empty,
                    ImmutableArray<KeyValuePair<string, TypedConstant>>.Empty);
            }

            return base.TrySynthesizeIsUnmanagedAttribute();
        }

        protected override SynthesizedAttributeData TrySynthesizeIsByRefLikeAttribute()
        {
            if ((object)_lazyIsByRefLikeAttribute != null)
            {
                return new SynthesizedAttributeData(
                    _lazyIsByRefLikeAttribute.Constructors[0],
                    ImmutableArray<TypedConstant>.Empty,
                    ImmutableArray<KeyValuePair<string, TypedConstant>>.Empty);
            }

            return base.TrySynthesizeIsByRefLikeAttribute();
        }

        private void CreateEmbeddedAttributesIfNeeded(DiagnosticBag diagnostics)
        {
            if (this.NeedsGeneratedIsReadOnlyAttribute)
            {
                CreateEmbeddedAttributeIfNeeded(
                    ref _lazyIsReadOnlyAttribute,
                    diagnostics,
                    AttributeDescription.IsReadOnlyAttribute);
            }

            if (this.NeedsGeneratedIsByRefLikeAttribute)
            {
                CreateEmbeddedAttributeIfNeeded(
                    ref _lazyIsByRefLikeAttribute,
                    diagnostics,
                    AttributeDescription.IsByRefLikeAttribute);
            }

            if (this.NeedsGeneratedIsUnmanagedAttribute)
            {
                CreateEmbeddedAttributeIfNeeded(
                    ref _lazyIsUnmanagedAttribute,
                    diagnostics,
                    AttributeDescription.IsUnmanagedAttribute);
            }

            if (this.NeedsGeneratedNullableAttribute)
            {
                CreateEmbeddedAttributeIfNeeded(
                    ref _lazyNullableAttribute,
                    diagnostics,
                    AttributeDescription.NullableAttribute,
                    GetAdditionalNullableAttributeConstructors);
            }
        }

        protected override bool InjectedSymbolsAreFrozen => !_lazyInjectedTypes.IsDefault;

        /// <summary>
        /// Get injected types that are needed (ie. were used) and report the diagnostics they held onto (only once).
        /// </summary>
        protected sealed override ImmutableArray<NamedTypeSymbol> GetInjectedTypes(DiagnosticBag diagnostics)
        {
            if (!_lazyInjectedTypes.IsDefault)
            {
                return _lazyInjectedTypes;
            }

            if (_needsNonNullTypesAttribute)
            {
                EnsureEmbeddedAttributeExists();
            }

            ArrayBuilder<NamedTypeSymbol> builder = null;
            if (_needsEmbeddedAttribute)
            {
                addInjectedAttribute(WellKnownType.Microsoft_CodeAnalysis_EmbeddedAttribute, canUseFromSource: false);
            }

            if (_needsNonNullTypesAttribute)
            {
                addInjectedAttribute(WellKnownType.System_Runtime_CompilerServices_NonNullTypesAttribute, canUseFromSource: true);
            }

            var result = builder is null ? ImmutableArray<NamedTypeSymbol>.Empty : builder.ToImmutableAndFree();
            ImmutableInterlocked.InterlockedInitialize(ref _lazyInjectedTypes, result);
            return _lazyInjectedTypes;

            void addInjectedAttribute(WellKnownType wellKnownType, bool canUseFromSource)
            {
                NamedTypeSymbol attribute = Compilation.GetWellKnownType(wellKnownType);

                if (attribute is InjectedAttributeSymbol injected)
                {
                    injected.AddDiagnostics(diagnostics);
                    builder = builder ?? ArrayBuilder<NamedTypeSymbol>.GetInstance();
                    builder.Add(attribute);
                }
                else if (attribute.IsErrorType())
                {
                    diagnostics.Add(attribute.GetUseSiteDiagnostic(), NoLocation.Singleton);
                }
                else if (!canUseFromSource)
                {
                    // if the attribute is defined in source, we can't embed it (we'll produce a diagnostic)
                    diagnostics.Add(ErrorCode.ERR_TypeReserved, attribute.Locations[0], attribute);
                }
            }
        }

        private void CreateEmbeddedAttributeIfNeeded(
            ref SynthesizedEmbeddedAttributeSymbol symbol,
            DiagnosticBag diagnostics,
            AttributeDescription description,
            Func<CSharpCompilation, NamedTypeSymbol, DiagnosticBag, ImmutableArray<MethodSymbol>> getAdditionalConstructors = null)
        {
            if ((object)symbol == null)
            {
                EnsureEmbeddedAttributeExists();

                var attributeMetadataName = MetadataTypeName.FromFullName(description.FullName);
                var userDefinedAttribute = _sourceAssembly.SourceModule.LookupTopLevelMetadataType(ref attributeMetadataName);
                Debug.Assert((object)userDefinedAttribute.ContainingModule == _sourceAssembly.SourceModule);

                if (!(userDefinedAttribute is MissingMetadataTypeSymbol))
                {
                    diagnostics.Add(ErrorCode.ERR_TypeReserved, userDefinedAttribute.Locations[0], description.FullName);
                }

                symbol = new SynthesizedEmbeddedAttributeSymbol(description, _sourceAssembly.DeclaringCompilation, getAdditionalConstructors, diagnostics);
            }
        }

        private static ImmutableArray<MethodSymbol> GetAdditionalNullableAttributeConstructors(
            CSharpCompilation compilation,
            NamedTypeSymbol containingType,
            DiagnosticBag diagnostics)
        {
            var boolType = compilation.GetSpecialType(SpecialType.System_Boolean);
            Binder.ReportUseSiteDiagnostics(boolType, diagnostics, Location.None);
            var boolArray = TypeSymbolWithAnnotations.Create(
                ArrayTypeSymbol.CreateSZArray(
                    boolType.ContainingAssembly,
                    TypeSymbolWithAnnotations.Create(boolType)));
            return ImmutableArray.Create<MethodSymbol>(
                new SynthesizedEmbeddedAttributeConstructorSymbol(
                    containingType,
                    m => ImmutableArray.Create(SynthesizedParameterSymbol.Create(m, boolArray, 0, RefKind.None))));
        }
    }

    internal sealed class PEAssemblyBuilder : PEAssemblyBuilderBase
    {
        public PEAssemblyBuilder(
            SourceAssemblySymbol sourceAssembly,
            EmitOptions emitOptions,
            OutputKind outputKind,
            Cci.ModulePropertiesForSerialization serializationProperties,
            IEnumerable<ResourceDescription> manifestResources)
            : base(sourceAssembly, emitOptions, outputKind, serializationProperties, manifestResources, ImmutableArray<NamedTypeSymbol>.Empty)
        {
        }

        public override int CurrentGenerationOrdinal => 0;
    }
}
