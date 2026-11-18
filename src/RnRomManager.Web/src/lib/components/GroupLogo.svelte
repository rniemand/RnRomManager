<script lang="ts">
	import { SvelteSet } from 'svelte/reactivity';
	import { logoSources, type LogoKind } from '../discovery';

	/**
	 * B16: a publisher's, genre's or system's logo: the first source that loads (shared logo, then starter art), else a
	 * text banner with the name. Decorative (`aria-hidden`): the name always follows as text.
	 */
	interface Props {
		kind: LogoKind;
		item: { key: string; name: string; logoUrl?: string | null };
		width?: number;
		height?: number;
	}

	let { kind, item, width = 100, height = 28 }: Props = $props();

	const failed = new SvelteSet<string>();
	const source = $derived(logoSources(kind, item, failed)[0]);
</script>

<!-- B14: one fixed frame for every source, so shared logos, starter art and the banner line up. -->
<span
	class="group-logo flex-shrink-0 border rounded bg-body-tertiary"
	aria-hidden="true"
	style:width="{width}px"
	style:height="{height}px"
>
	{#if source}
		<img src={source} alt="" {width} {height} loading="lazy" onerror={() => failed.add(source)} />
	{:else}
		<!-- text-body: the frame is light even in a selected (white-text) row. -->
		<span class="group-logo-banner small text-truncate text-body" title={item.name}
			>{item.name}</span
		>
	{/if}
</span>

<style>
	.group-logo {
		display: inline-flex;
	}
	.group-logo img {
		object-fit: contain;
		width: 100%;
		height: 100%;
	}
	.group-logo-banner {
		display: flex;
		align-items: center;
		justify-content: center;
		width: 100%;
		height: 100%;
		padding: 0 0.25rem;
		font-weight: 600;
		line-height: 1;
	}
</style>
