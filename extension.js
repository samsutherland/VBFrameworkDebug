'use strict';

const fs = require('node:fs');
const path = require('node:path');
const vscode = require('vscode');

function activate(context) {
    context.subscriptions.push(vscode.debug.registerDebugAdapterDescriptorFactory('desktopclr', {
        createDebugAdapterDescriptor(session) {
            if (process.platform !== 'win32' || vscode.env.remoteName) {
                throw new Error('VB.NET Framework debugging requires a local Windows VS Code workspace.');
            }
            const architecture = session.configuration.architecture ?? 'x64';
            if (architecture !== 'x64' && architecture !== 'x86') {
                throw new Error(`Unknown debuggee architecture: ${architecture}. Expected x64 or x86.`);
            }
            const executable = context.asAbsolutePath(path.join('bin', architecture, 'DesktopClr.Adapter.exe'));
            if (!fs.existsSync(executable)) {
                throw new Error('The desktop CLR adapter is missing. Install the packaged VSIX or run scripts\\build.ps1.');
            }
            return new vscode.DebugAdapterExecutable(executable);
        }
    }));
}

module.exports = { activate };
