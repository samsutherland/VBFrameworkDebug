param([switch]$Package)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    $project = 'adapter\DesktopClr.Adapter.csproj'
    $assetsPath = 'adapter\obj\project.assets.json'
    foreach ($architecture in 'x64', 'x86') {
        dotnet build $project -c Release "-p:Platform=$architecture" "-p:PlatformTarget=$architecture" -o "bin\$architecture"
    }

    $assets = Get-Content $assetsPath -Raw | ConvertFrom-Json
    foreach ($architecture in 'x64', 'x86') {
        $notices = New-Item "bin\$architecture\licenses" -ItemType Directory -Force
        $target = $assets.targets.PSObject.Properties | Where-Object Name -In 'net48', '.NETFramework,Version=v4.8' | Select-Object -First 1
        $runtimePackages = @($target.Value.PSObject.Properties |
            Where-Object { $_.Value.runtime.PSObject.Properties.Name -like '*.dll' })
        if (!$runtimePackages.Count) { throw 'No runtime package licenses could be resolved.' }
        foreach ($dependency in $runtimePackages) {
            $relative = $assets.libraries.PSObject.Properties[$dependency.Name].Value.path
            $location = $assets.packageFolders.PSObject.Properties.Name | ForEach-Object { Join-Path $_ $relative } |
                Where-Object { Test-Path $_ -PathType Container } | Select-Object -First 1
            if (!$location) { throw "Cannot find restored package $($dependency.Name)" }
            $license = Get-ChildItem -LiteralPath $location -File | Where-Object Name -Like 'LICENSE*'
            if (!$license) { throw "No license found for $($dependency.Name)" }
            $notice = Get-ChildItem -LiteralPath $location -File | Where-Object Name -Like '*NOTICES*'
            $prefix = $dependency.Name.Replace('/', '-')
            foreach ($file in @($license; $notice)) {
                Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $notices.FullName "$prefix-$($file.Name)")
            }
        }
    }

    if ($Package) {
        $version = (Get-Content package.json -Raw | ConvertFrom-Json).version
        New-Item 'dist' -ItemType Directory -Force | Out-Null
        npx --yes --package '@vscode/vsce@4.1.1' vsce package --target win32-x64 --no-dependencies `
            --allow-missing-repository --out "dist\vb-framework-debug-$version-win32-x64.vsix"
    }
}
finally {
    Pop-Location
}
