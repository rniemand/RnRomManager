<script lang="ts">
	import { formatSize } from '../format';

	interface Props {
		bytes: number | null | undefined;
	}

	let { bytes }: Props = $props();

	const text = $derived(formatSize(bytes));
</script>

{#if text}
	<span class="file-size font-monospace text-nowrap" title="{bytes!.toLocaleString()} bytes"
		>{text}</span
	>
{/if}
