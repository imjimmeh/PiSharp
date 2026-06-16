/**
 * Tests that the TsBridge runner survives SIGINT (Ctrl+C) and exits cleanly
 * when stdin closes (parent process gone).
 *
 * Run with: node --test test/sigint-survival.mjs
 *
 * Windows note: child.kill('SIGINT') on Windows does not deliver CTRL_C_EVENT to
 * pipe-isolated child processes — it calls TerminateProcess instead. Signal handler
 * registration is therefore verified via the _debug_signal_handlers RPC introspection
 * method. Survival after a real CTRL_C (which Windows delivers through the shared
 * console driver) is validated in the production scenario by the C# integration tests.
 */

import { test } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import path from "node:path";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const RUNNER_PATH = path.resolve(__dirname, "../TsBridgeRunner.mjs");

/** Milliseconds to wait after sending a signal before checking exit status. */
const SIGINT_SETTLE_MS = 300;

/** Maximum milliseconds to wait for the process to exit after stdin closes. */
const STDIN_CLOSE_TIMEOUT_MS = 2_000;

/**
 * Sends a JSON-RPC request to the child process stdin and waits for a
 * response line on stdout. Returns the parsed response object.
 *
 * @param {import("node:child_process").ChildProcess} child
 * @param {object} request
 * @param {number} [timeoutMs]
 * @returns {Promise<object>}
 */
function sendRpc(child, request, timeoutMs = 5_000) {
	return new Promise((resolve, reject) => {
		let buffer = "";
		let settled = false;

		const timer = setTimeout(() => {
			if (!settled) {
				settled = true;
				child.stdout.off("data", onData);
				reject(new Error(`RPC timed out after ${timeoutMs}ms: ${JSON.stringify(request)}`));
			}
		}, timeoutMs);

		function onData(chunk) {
			buffer += chunk.toString();
			const newline = buffer.indexOf("\n");
			if (newline !== -1) {
				const line = buffer.slice(0, newline);
				buffer = buffer.slice(newline + 1);
				if (!settled) {
					settled = true;
					clearTimeout(timer);
					child.stdout.off("data", onData);
					try {
						resolve(JSON.parse(line));
					} catch (err) {
						reject(new Error(`Failed to parse RPC response: ${line}`));
					}
				}
			}
		}

		child.stdout.on("data", onData);
		child.stdin.write(JSON.stringify(request) + "\n");
	});
}

/**
 * Waits for a child process to exit, resolving with the exit code.
 *
 * @param {import("node:child_process").ChildProcess} child
 * @param {number} timeoutMs
 * @returns {Promise<number | null>}
 */
function waitForExit(child, timeoutMs) {
	return new Promise((resolve) => {
		if (child.exitCode !== null) {
			resolve(child.exitCode);
			return;
		}

		const timer = setTimeout(() => {
			child.off("exit", onExit);
			resolve(null); // timed out — process still alive
		}, timeoutMs);

		function onExit(code) {
			clearTimeout(timer);
			resolve(code);
		}

		child.once("exit", onExit);
	});
}

/**
 * Spawns the TsBridge runner with piped stdio.
 *
 * @returns {import("node:child_process").ChildProcess}
 */
function spawnRunner() {
	return spawn(process.execPath, [RUNNER_PATH], {
		stdio: "pipe",
		detached: false,
	});
}

/**
 * Initialises the runner and returns the child process.
 *
 * @returns {Promise<import("node:child_process").ChildProcess>}
 */
async function spawnAndInitialize() {
	const child = spawnRunner();

	const initResponse = await sendRpc(child, {
		jsonrpc: "2.0",
		id: 1,
		method: "initialize",
		params: {
			extensionPaths: [],
			session: {},
			commands: [],
		},
	});

	assert.equal(initResponse.id, 1, "initialize response id should be 1");
	assert.ok(initResponse.result?.ok, "initialize should succeed");

	return child;
}

test("runner registers SIGINT and SIGBREAK handlers to survive Ctrl+C", async () => {
	// This test verifies that the runner installs no-op handlers for SIGINT and
	// SIGBREAK. On Windows, child.kill('SIGINT') calls TerminateProcess and cannot
	// be intercepted by JS handlers; we therefore use the _debug_signal_handlers
	// introspection RPC which reports process.listenerCount for each signal.
	// Before the fix: listenerCount is 0 → the assertion below fails (RED).
	// After the fix: listenerCount is 1 → passes (GREEN).

	const child = await spawnAndInitialize();

	try {
		const handlersResponse = await sendRpc(child, {
			jsonrpc: "2.0",
			id: 2,
			method: "_debug_signal_handlers",
			params: {},
		});

		assert.equal(handlersResponse.id, 2, "_debug_signal_handlers response id should be 2");

		const { sigint, sigbreak } = handlersResponse.result;

		assert.ok(
			sigint >= 1,
			`Expected at least 1 SIGINT handler registered (got ${sigint}). ` +
			"The runner must install process.on('SIGINT') to survive Ctrl+C on Windows.",
		);

		assert.ok(
			sigbreak >= 1,
			`Expected at least 1 SIGBREAK handler registered (got ${sigbreak}). ` +
			"The runner must install process.on('SIGBREAK') to survive Ctrl+Break on Windows.",
		);

		// Additionally, on non-Windows platforms, verify actual signal survival
		// by sending SIGINT and confirming the runner still responds to RPC.
		if (process.platform !== "win32") {
			child.kill("SIGINT");
			await new Promise((resolve) => setTimeout(resolve, SIGINT_SETTLE_MS));

			assert.equal(
				child.exitCode,
				null,
				`Runner should still be alive ${SIGINT_SETTLE_MS}ms after SIGINT`,
			);

			const uiResponse = await sendRpc(child, {
				jsonrpc: "2.0",
				id: 3,
				method: "set_runtime_has_ui",
				params: { hasUi: false },
			});

			assert.equal(uiResponse.id, 3, "set_runtime_has_ui response id should be 3");
		}
	} finally {
		child.kill("SIGTERM");
		await waitForExit(child, 2_000);
	}
});

test("runner exits cleanly when stdin closes", async () => {
	const child = await spawnAndInitialize();

	// Close stdin to simulate the parent process disappearing.
	child.stdin.end();

	// The runner must exit with code 0 within the timeout.
	const exitCode = await waitForExit(child, STDIN_CLOSE_TIMEOUT_MS);

	assert.equal(
		exitCode,
		0,
		`Runner should exit with code 0 after stdin closes; actual exitCode=${exitCode}`,
	);
});
