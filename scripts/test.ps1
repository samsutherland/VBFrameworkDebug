param([switch]$VSCode)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    & "$PSScriptRoot\build.ps1" -Package:$VSCode
    foreach ($architecture in 'x64', 'x86') {
        dotnet build 'tests\Fixture\Fixture.vbproj' "-p:Platform=$architecture" "-p:PlatformTarget=$architecture" -o "dist\fixtures\$architecture-portable"
        $legacy = Join-Path (Get-Location) "dist\fixtures\$architecture-windows\"
        dotnet msbuild 'tests\LegacyFixture\LegacyFixture.vbproj' "-p:Platform=$architecture" "-p:PlatformTarget=$architecture" "-p:OutputPath=$legacy"
        foreach ($format in 'portable', 'windows') {
            python 'tests\smoke.py' --adapter "bin\$architecture\DesktopClr.Adapter.exe" `
                --fixture "dist\fixtures\$architecture-$format\Fixture.exe"
        }
    }

    dotnet build 'tests\Fixture\Fixture.vbproj' -p:Platform=Prefer32 -p:PlatformTarget=AnyCPU -p:Prefer32Bit=true -o 'dist\fixtures\prefer32'
    python 'tests\smoke.py' --adapter 'bin\x86\DesktopClr.Adapter.exe' --fixture 'dist\fixtures\prefer32\Fixture.exe'
    python 'tests\smoke.py' --adapter 'bin\x64\DesktopClr.Adapter.exe' --fixture 'dist\fixtures\prefer32\Fixture.exe' --mode wrong-bitness
    python 'tests\smoke.py' --adapter 'bin\x64\DesktopClr.Adapter.exe' --fixture 'dist\fixtures\x64-portable\Fixture.exe' --mode disconnect

    foreach ($format in 'portable', 'windows') {
        $negative = New-Item "dist\fixtures\negative-$format" -ItemType Directory -Force
        Copy-Item "dist\fixtures\x64-$format\Fixture.exe" $negative.FullName -Force
        $pdb = Join-Path $negative.FullName 'Fixture.pdb'
        if (Test-Path -LiteralPath $pdb) { Remove-Item -LiteralPath $pdb }
        python 'tests\smoke.py' --adapter 'bin\x64\DesktopClr.Adapter.exe' --fixture (Join-Path $negative.FullName 'Fixture.exe') --mode missing
        Copy-Item "dist\fixtures\x86-$format\Fixture.pdb" $pdb -Force
        python 'tests\smoke.py' --adapter 'bin\x64\DesktopClr.Adapter.exe' --fixture (Join-Path $negative.FullName 'Fixture.exe') --mode mismatched
    }

    if ($VSCode) {
        $root = (Get-Location).Path
        $profile = New-Item (Join-Path $root "dist\vscode-test-$([guid]::NewGuid())") -ItemType Directory
        $previousReport = $env:VB_FRAMEWORK_DEBUG_TEST_REPORT
        $report = Join-Path $profile.FullName 'result.txt'
        try {
            $env:VB_FRAMEWORK_DEBUG_TEST_REPORT = $report
            $version = (Get-Content package.json -Raw | ConvertFrom-Json).version
            $vsix = Join-Path $root "dist\vb-framework-debug-$version-win32-x64.vsix"
            $profileArgs = @('--user-data-dir', (Join-Path $profile.FullName 'user'),
                '--extensions-dir', (Join-Path $profile.FullName 'extensions'))
            code @profileArgs --install-extension $vsix
            code @profileArgs --skip-welcome --disable-workspace-trust --wait `
                "--extensionDevelopmentPath=$root\tests" "--extensionTestsPath=$root\tests\vscode-smoke.js"
            if (!(Test-Path -LiteralPath $report)) { throw "VS Code smoke test did not report success. See $($profile.FullName)." }
            Get-Content -LiteralPath $report
            Remove-Item -LiteralPath $profile.FullName -Recurse -Force
        }
        finally {
            $env:VB_FRAMEWORK_DEBUG_TEST_REPORT = $previousReport
        }
    }
}
finally {
    Pop-Location
}
