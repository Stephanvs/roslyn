﻿' Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports Microsoft.CodeAnalysis.Editor.VisualBasic.EndConstructGeneration

Namespace Microsoft.CodeAnalysis.Editor.VisualBasic.UnitTests.EndConstructGeneration
    Public Class ITextSnapshotExtensionsTests
        <Fact, Trait(Traits.Feature, Traits.Features.EndConstructGeneration)>
        Public Sub ThrowsWithNullSnapshot()
            Assert.Throws(Of ArgumentNullException)(Sub()
                                                        EndConstructExtensions.GetAligningWhitespace(Nothing, 0)
                                                    End Sub)
        End Sub
    End Class
End Namespace
