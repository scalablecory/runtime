// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace System.Net.Http.Primitives
{
    public enum HttpReadType
    {
        None, // default/uninitialized.

        Response,
        Headers,
        Content,
        TrailingHeaders,
        EndOfStream,

        AltSvc // The ALTSVC extension frame in HTTP/2.
    }
}
