'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vscode = require('vscode');

async function run() {
    const root = path.dirname(__dirname);
    const installed = vscode.extensions.getExtension('vbframeworkdebug.vb-framework-debug');
    assert.ok(installed, 'The VSIX extension is not installed.');
    assert.notEqual(installed.extensionPath.toLowerCase(), root.toLowerCase(), 'Expected the packaged extension.');
    const source = vscode.Uri.file(path.join(__dirname, 'Fixture', 'Program.vb'));
    const document = await vscode.workspace.openTextDocument(source);
    assert.equal(document.languageId, 'vb');
    const line = document.getText().split(/\r?\n/).findIndex(text => text.includes("' CALL"));
    const breakpoint = new vscode.SourceBreakpoint(new vscode.Location(source, new vscode.Position(line, 0)),
        true, 'answer = 41', '== 1');
    const disposables = [];
    let timer;
    try {
        const stopped = new Promise((resolve, reject) => {
            timer = setTimeout(() => reject(new Error('Timed out waiting for the VS Code breakpoint.')), 12000);
            disposables.push(vscode.debug.registerDebugAdapterTrackerFactory('desktopclr', {
                createDebugAdapterTracker(session) {
                    return {
                        onDidSendMessage(message) {
                            if (message.event === 'stopped') resolve({session, ...message.body});
                        },
                        onError: reject,
                        onExit(code) { reject(new Error(`Adapter exited before the breakpoint: ${code}.`)); }
                    };
                }
            }));
        });
        vscode.debug.addBreakpoints([breakpoint]);
        const launched = vscode.debug.startDebugging(undefined, {
            type: 'desktopclr', request: 'launch', name: 'VS Code integration test',
            program: path.join(root, 'dist', 'fixtures', 'x64-portable', 'Fixture.exe')
        }).then(result => assert.equal(result, true));
        const [{session, threadId, reason}] = await Promise.all([stopped, launched]);
        assert.equal(reason, 'breakpoint');
        const stack = await session.customRequest('stackTrace', {threadId});
        assert.equal(stack.stackFrames[0].line, line + 1);
        const scopes = await session.customRequest('scopes', {frameId: stack.stackFrames[0].id});
        const locals = await session.customRequest('variables', {variablesReference: scopes.scopes[0].variablesReference});
        assert.equal(locals.variables.find(variable => variable.name === 'answer').value, '41');
        const evaluated = await session.customRequest('evaluate', {
            expression: 'environment', frameId: stack.stackFrames[0].id, context: 'repl'
        });
        assert.equal(evaluated.result, '"Test"');
        const expression = await session.customRequest('evaluate', {
            expression: 'sample.Count + numbers(1)', frameId: stack.stackFrames[0].id, context: 'watch'
        });
        assert.equal(expression.result, '31');
        const modules = await session.customRequest('modules', {});
        assert.ok(modules.modules.some(module => module.symbolStatus === 'Symbols loaded'));
        const locations = await session.customRequest('breakpointLocations', {source: {path: source.fsPath}, line: line + 1});
        assert.equal(locations.breakpoints[0].line, line + 1);
        const args = await session.customRequest('evaluate', {expression: 'numbers', frameId: stack.stackFrames[0].id});
        const page = await session.customRequest('variables', {variablesReference: args.variablesReference, start: 1, count: 1});
        assert.equal(page.variables[0].value, '24');
        assert.ok(!vscode.extensions.getExtension('ms-dotnettools.csharp'));
        const report = process.env.VB_FRAMEWORK_DEBUG_TEST_REPORT;
        if (report) fs.writeFileSync(report, 'VS Code debugger smoke passed\n');
    } finally {
        clearTimeout(timer);
        await vscode.debug.stopDebugging();
        vscode.debug.removeBreakpoints([breakpoint]);
        for (const disposable of disposables) disposable.dispose();
    }
}

module.exports = {run};
