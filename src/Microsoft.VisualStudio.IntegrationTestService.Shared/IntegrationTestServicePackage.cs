// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for more information.

namespace Microsoft.VisualStudio.IntegrationTestService
{
    using System;
    using System.Runtime.InteropServices;
#if NET472_OR_GREATER
    using System.Threading;
    using System.Threading.Tasks;
#endif
    using Microsoft.VisualStudio.Shell;

#if NET472_OR_GREATER
    [Guid("78D5A8B5-1634-434B-802D-E3E4A46B1AA6")]
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideMenuResource("Menus.ctmenu", version: 1)]
    public sealed class IntegrationTestServicePackage : AsyncPackage
    {
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            IntegrationTestServiceCommands.Initialize(this);
        }
    }
#else
    // Dev11 (net45 / Shell.11) predates AsyncPackage.
    [Guid("78D5A8B5-1634-434B-802D-E3E4A46B1AA6")]
    [PackageRegistration(UseManagedResourcesOnly = true)]
    [ProvideMenuResource("Menus.ctmenu", version: 1)]
    public sealed class IntegrationTestServicePackage : Package
    {
        protected override void Initialize()
        {
            base.Initialize();
            IntegrationTestServiceCommands.Initialize(this);
        }
    }
#endif
}
