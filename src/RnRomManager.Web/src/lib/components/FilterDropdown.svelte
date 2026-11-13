<script lang="ts" module>
	/** One menu row; `search` adds texts the filter input also matches (e.g. a system's short name). */
	export interface FilterOption {
		value: string;
		label: string;
		/** System menu (L3): a `SystemBadge` before the label. */
		badge?: { system: string; name: string };
		search?: string[];
	}
</script>

<script lang="ts">
	import { tick } from 'svelte';
	import { Dropdown } from '#lib/bootstrap.js';
	import { formatCount } from '#lib/format.js';
	import type { FacetCounts } from '#lib/libraryQuery.js';
	import SystemBadge from './SystemBadge.svelte';

	interface Props {
		/** The field name: `System`, `Region`, `Letter`, `Sort`. */
		label: string;
		options: FilterOption[];
		/** The chosen option's value; null = the "All …" row. */
		value: string | null;
		/**
		 * Counts by option value (L2): null while loading or after a failed call (no counts shown); leave out for a
		 * menu without counts (Sort).
		 */
		counts?: FacetCounts;
		/** The "All …" row, first; `sum` shows the sum of the counts (not for Region, whose counts overlap). */
		all?: { label: string; sum: boolean };
		/** Filled toggle; defaults to "a value is chosen". Sort passes "not the default sort". */
		active?: boolean;
		/** Placeholder of a filter input at the top of the menu (L3); none when left out. */
		filterPlaceholder?: string;
		onselect: (value: string | null) => void;
	}

	let {
		label,
		options,
		value,
		counts,
		all,
		active = value !== null,
		filterPlaceholder,
		onselect
	}: Props = $props();

	let root = $state<HTMLDivElement>();
	let toggle = $state<HTMLButtonElement>();
	let filterInput = $state<HTMLInputElement>();
	let filter = $state('');

	const chosen = $derived(options.find((o) => o.value === value));
	const shown = $derived.by(() => {
		const needle = filter.trim().toLowerCase();
		if (!needle) return options;
		return options.filter((o) =>
			[o.label, o.value, ...(o.search ?? [])].some((t) => t.toLowerCase().includes(needle))
		);
	});
	const allCount = $derived(
		all?.sum && counts ? Object.values(counts).reduce((sum, n) => sum + n, 0) : null
	);

	function countOf(key: string): number | null {
		return counts ? (counts[key] ?? 0) : null;
	}

	function choose(next: string | null): void {
		onselect(next);
		if (toggle) {
			Dropdown.getOrCreateInstance(toggle).hide();
			toggle.focus();
		}
	}

	// Bootstrap's own events (not typed as Svelte handlers): focus the filter on open, clear it on close.
	$effect(() => {
		const el = root;
		if (!el) return;
		const shownHandler = async () => {
			await tick();
			filterInput?.focus();
		};
		const hiddenHandler = () => (filter = '');
		el.addEventListener('shown.bs.dropdown', shownHandler);
		el.addEventListener('hidden.bs.dropdown', hiddenHandler);
		return () => {
			el.removeEventListener('shown.bs.dropdown', shownHandler);
			el.removeEventListener('hidden.bs.dropdown', hiddenHandler);
		};
	});
</script>

<div class="dropdown" bind:this={root}>
	<button
		bind:this={toggle}
		type="button"
		class="btn btn-sm dropdown-toggle {active ? 'btn-secondary' : 'btn-outline-secondary'}"
		data-bs-toggle="dropdown"
		aria-expanded="false"
		aria-label="{label}: {chosen?.label ?? 'All'}"
	>
		{label}{#if chosen}: {chosen.label}{/if}
	</button>
	<ul class="dropdown-menu filter-menu">
		{#if filterPlaceholder}
			<li class="px-2 pb-2">
				<input
					bind:this={filterInput}
					bind:value={filter}
					type="search"
					class="form-control form-control-sm"
					placeholder={filterPlaceholder}
					aria-label={filterPlaceholder}
				/>
			</li>
		{/if}
		{#if all}
			<li>
				<button
					type="button"
					class="dropdown-item d-flex justify-content-between align-items-center gap-3"
					class:active={value === null}
					aria-current={value === null ? 'true' : undefined}
					onclick={() => choose(null)}
				>
					<span>{all.label}</span>
					{#if allCount !== null}
						<span class="ms-auto text-body-secondary font-monospace">{formatCount(allCount)}</span>
					{/if}
				</button>
			</li>
		{/if}
		{#each shown as option (option.value)}
			{@const count = countOf(option.value)}
			<li>
				<button
					type="button"
					class="dropdown-item d-flex justify-content-between align-items-center gap-3"
					class:active={value === option.value}
					class:text-body-secondary={count === 0 && value !== option.value}
					aria-current={value === option.value ? 'true' : undefined}
					onclick={() => choose(option.value)}
				>
					<span class="d-inline-flex align-items-center gap-2">
						{#if option.badge}<SystemBadge
								system={option.badge.system}
								name={option.badge.name}
							/>{/if}{option.label}
					</span>
					{#if count !== null}
						<span class="ms-auto text-body-secondary font-monospace">{formatCount(count)}</span>
					{/if}
				</button>
			</li>
		{/each}
	</ul>
</div>

<style>
	.filter-menu {
		min-width: 16rem;
		max-height: 20rem;
		overflow-y: auto;
	}
</style>
