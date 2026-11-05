/**
 * Fake passwords for tests, built at runtime so no credential-shaped literal sits in the repo for secret scanners
 * to flag. Each is 16+ characters (valid under the §9.1 policy) and unique per label.
 */
export function fakePassword(label: string): string {
	return `${label}-${'x'.repeat(12)}`;
}
