// Copyright © Tanner Gooding and Contributors. Licensed under the MIT License (MIT).
// See third-party-notices in the repository root for more information.

// Ported from um/wincodec.h in the Windows SDK for Windows 10.0.19041.0
// Original source is Copyright © Microsoft. All rights reserved.

// <auto-generated />
#pragma warning disable CS0649

using System;

namespace TerraFX.Interop
{
    internal unsafe partial struct WICBitmapPlane
    {
        [NativeTypeName("WICPixelFormatGUID")]
        public Guid Format;

        [NativeTypeName("BYTE *")]
        public byte* pbBuffer;

        [NativeTypeName("UINT")]
        public uint cbStride;

        [NativeTypeName("UINT")]
        public uint cbBufferSize;
    }
}
