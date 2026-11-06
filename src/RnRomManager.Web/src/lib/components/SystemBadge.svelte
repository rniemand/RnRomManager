<script lang="ts">
	import Icon from './Icon.svelte';

	interface Props {
		/** System id (`gb`, `gbc`, `gba`, …). */
		system: string;
		/** Display name; the upper-cased id when missing. */
		name?: string;
	}

	let { system, name }: Props = $props();

	// A fixed colour per system so a list scans at a glance; unknown systems stay neutral.
	const colours: Record<string, string> = {
		gb: 'text-bg-success',
		gbc: 'text-bg-info',
		gba: 'text-bg-primary'
	};
</script>

<span class="badge {colours[system] ?? 'text-bg-secondary'} d-inline-flex align-items-center gap-1">
	<Icon name="system-{system}" />{name ?? system.toUpperCase()}
</span>
