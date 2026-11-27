import { execFile } from 'child_process';
import type { ScanResultJson } from './providers/treeViewProvider';

export interface RunResult {
    success: boolean;
    /** Process exit code: 0 = clean, 1 = unused packages found, 2 = error; -1 = could not start. */
    exitCode: number;
    data?: ScanResultJson;
    error?: string;
    /** True when the CLI executable could not be found at all. */
    notFound?: boolean;
}

/**
 * Runs the slimdep CLI tool as a subprocess and returns parsed output.
 */
export async function runSlimdep(toolPath: string, args: string[]): Promise<RunResult> {
    return new Promise((resolve) => {
        execFile(toolPath, args, { encoding: 'utf8', timeout: 60_000 }, (error, stdout, stderr) => {
            // For a non-zero exit, error.code is the numeric exit code; when the process
            // could not be started it is a string such as 'ENOENT'.
            const code = (error as NodeJS.ErrnoException | null)?.code as unknown;
            const exitCode = error ? (typeof code === 'number' ? code : -1) : 0;

            // execFile launches the executable directly (no shell), so a missing CLI surfaces
            // only as ENOENT. Don't match stderr text: real scan errors such as
            // "project.assets.json not found" must not be mistaken for a missing CLI.
            if (code === 'ENOENT') {
                resolve({ success: false, exitCode, notFound: true, error: error?.message });
                return;
            }

            // slimdep exits 1 when unused packages are found — that is a successful scan.
            if (exitCode !== 0 && exitCode !== 1) {
                resolve({ success: false, exitCode, error: stderr.trim() || stdout.trim() || error?.message });
                return;
            }

            if (args.includes('--format') && args.includes('json')) {
                try {
                    resolve({ success: true, exitCode, data: parseScanJson(stdout) });
                } catch {
                    resolve({ success: false, exitCode, error: `Failed to parse JSON output: ${stdout}` });
                }
            } else {
                resolve({ success: true, exitCode });
            }
        });
    });
}

/**
 * Parses `slimdep scan --format json` output. Skips any leading progress lines
 * (printed to stdout by SlimDep 1.1.0 and earlier) and accepts either a single
 * result object or an array of results (multi-project scans).
 */
function parseScanJson(stdout: string): ScanResultJson {
    const lines = stdout.split(/\r?\n/);
    const start = lines.findIndex(l => /^\s*[[{]/.test(l));
    const json = start >= 0 ? lines.slice(start).join('\n') : stdout;
    const parsed = JSON.parse(json) as ScanResultJson | ScanResultJson[];
    if (Array.isArray(parsed)) {
        if (parsed.length === 0) throw new Error('empty result');
        return parsed[0];
    }
    return parsed;
}
