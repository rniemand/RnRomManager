<script lang="ts">
	import JsonTree from './JsonTree.svelte';

	/**
	 * GP4: a read-only JSON tree. Objects and arrays are `<details>` (depth 0 and 1 start open); the root adds
	 * Expand all / Collapse all. Colours are Bootstrap emphasis classes, readable in both themes.
	 */
	interface Props {
		value: unknown;
		/** The key (object) or index (array) this value sits under; none for the root. */
		name?: string;
		depth?: number;
	}

	let { value, name, depth = 0 }: Props = $props();

	let container = $state<HTMLElement>();
	// svelte-ignore state_referenced_locally
	let open = $state(depth < 2);

	const isArray = $derived(Array.isArray(value));
	const isObject = $derived(value !== null && typeof value === 'object');
	const entries = $derived(
		isObject ? Object.entries(value as Record<string, unknown>) : ([] as [string, unknown][])
	);
	const count = $derived(isArray ? `[${entries.length}]` : `{${entries.length}}`);

	const primitiveClass = $derived(
		typeof value === 'string'
			? 'text-success-emphasis'
			: typeof value === 'number'
				? 'text-warning-emphasis'
				: 'text-danger-emphasis'
	);

	function setAll(next: boolean): void {
		container?.querySelectorAll('details').forEach((d) => (d.open = next));
	}
</script>

{#snippet key()}
	{#if name !== undefined}<span class="text-primary-emphasis">{name}</span>:&#32;{/if}
{/snippet}

{#if depth === 0}
	<div class="font-monospace small" bind:this={container}>
		<div class="d-flex gap-2 mb-2">
			<button type="button" class="btn btn-sm btn-outline-secondary" onclick={() => setAll(true)}
				>Expand all</button
			>
			<button type="button" class="btn btn-sm btn-outline-secondary" onclick={() => setAll(false)}
				>Collapse all</button
			>
		</div>
		{@render node()}
	</div>
{:else}
	{@render node()}
{/if}

{#snippet node()}
	{#if isObject}
		<details bind:open>
			<summary
				class="py-1"
				aria-label={name === undefined ? (isArray ? 'JSON array' : 'JSON object') : undefined}
				>{@render key()}{#if !open}<span class="text-body-secondary">{count}</span>{/if}</summary
			>
			<ul class="list-unstyled ms-3 mb-0">
				{#each entries as [childKey, child] (childKey)}
					<li><JsonTree value={child} name={childKey} depth={depth + 1} /></li>
				{/each}
			</ul>
		</details>
	{:else}
		<span>{@render key()}<span class={primitiveClass}>{JSON.stringify(value)}</span></span>
	{/if}
{/snippet}
