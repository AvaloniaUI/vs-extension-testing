// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for more information.

namespace Xunit
{
    public enum VisualStudioVersion
    {
        Unspecified,
        VS2012,
        VS2013,
        VS2015,
        VS2017,
        VS2019,
        VS2022,
        VS18,

        /// <summary>
        /// Visual Studio 18.11 only. Unlike the other values this pins the minor version, so it
        /// selects an 18.11 install when other 18.x installs are present.
        /// </summary>
        VS18_11,
    }
}
