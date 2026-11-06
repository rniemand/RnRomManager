<script lang="ts">
	import { systemColour } from '../systemColours';
	import Icon from './Icon.svelte';

	interface Props {
		/** System id (`gb`, `gbc`, `gba`, …). */
		system: string;
		/** Display name; the upper-cased id when missing. */
		name?: string;
	}

	let { system, name }: Props = $props();

	// A fixed colour per system so a list scans at a glance (shared with the Library Size treemap).
	const colour = $derived(systemColour(system).name);
</script>

<span class="badge text-bg-{colour} d-inline-flex align-items-center gap-1">
	<Icon name="system-{system}" />{name ?? system.toUpperCase()}
</span>
