import { execFile } from 'child_process';
import type { ScanResultJson } from './providers/treeViewProvider';

interface RunResult {
    success: boolean;
    data?: ScanResultJson;
    error?: string;
}

/**
 * Runs the slimdep CLI tool as a subprocess and returns parsed output.
 */
export async function runSlimdep(toolPath: string, args: string[]): Promise<RunResult> {
    return new Promise((resolve) => {
        execFile(toolPath, args, { encoding: 'utf8', timeout: 60_000 }, (error, stdout, stderr) => {
            if (error && !stdout) {
                resolve({ success: false, error: stderr || error.message });
                return;
            }

            // --format json returns JSON on stdout; non-zero exit = unused found
            if (args.includes('--format') && args.includes('json')) {
                try {
                    const data = JSON.parse(stdout.trim()) as ScanResultJson;
                    resolve({ success: true, data });
                } catch {
                    resolve({ success: false, error: `Failed to parse JSON output: ${stdout}` });
                }
            } else {
                resolve({ success: true });
            }
        });
    });
}
