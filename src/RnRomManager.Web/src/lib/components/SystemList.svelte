<script lang="ts">
	import { untrack } from 'svelte';
	import { resolve } from '$app/paths';
	import type { SystemInfo } from '../api/types';
	import { maxFind, systemsSearch } from '../discovery';
	import { formatCount } from '../format';
	import { auth } from '../stores/auth.svelte';
	import EmptyState from './EmptyState.svelte';
	import GroupLogo from './GroupLogo.svelte';
	import Icon from './Icon.svelte';

	/**
	 * Systems tab B3: every supported system (0 games included), each a link selecting it, with its logo (B18) and, for
	 * Admins, a Change logo button (B19).
	 */
	interface Props {
		systems: SystemInfo[];
		selected: string | null;
		/** B19: open the logo modal for a system. */
		onedit?: (system: SystemInfo) => void;
		/** B16: the URL's search text, absent = none. */
		find?: string;
		/** B16: a new search (trimmed, '' for none); the page writes it to the URL. */
		onfind?: (find: string) => void;
	}

	let { systems, selected, onedit, find, onfind }: Props = $props();

	/** B16: a client-side filter by display name or id (trimmed, case-insensitive contains). */
	const shown = $derived.by(() => {
		const needle = find?.toLowerCase();
		return needle
			? systems.filter(
					(s) => s.displayName.toLowerCase().includes(needle) || s.id.toLowerCase().includes(needle)
				)
			: systems;
	});

	let findText = $state(untrack(() => find ?? ''));
	let findTimer: ReturnType<typeof setTimeout> | undefined;
	function onFindInput(): void {
		clearTimeout(findTimer);
		findTimer = setTimeout(() => {
			if (findText.trim() !== (find ?? '')) onfind?.(findText.trim());
		}, 300);
	}
	// A pending search is dropped on unmount.
	$effect(() => () => clearTimeout(findTimer));
	// URL → box (Back/Forward).
	$effect(() => {
		const current = find ?? '';
		untrack(() => {
			if (findText.trim() !== current) findText = current;
		});
	});
</script>

<div class="mb-2">
	<label class="visually-hidden" for="find-systems">Search systems</label>
	<input
		id="find-systems"
		type="search"
		class="form-control form-control-sm"
		placeholder="Search systems"
		maxlength={maxFind}
		bind:value={findText}
		oninput={onFindInput}
	/>
</div>

{#if find && shown.length === 0}
	<EmptyState message="No systems match “{find}”." />
{/if}
<ul class="list-group" aria-label="Systems" hidden={shown.length === 0}>
	{#each shown as system (system.id)}
		<li class="list-group-item p-0 d-flex align-items-stretch">
			<a
				class="list-group-item-action flex-grow-1 min-w-0 d-flex align-items-center justify-content-between gap-2 px-3 py-2 text-decoration-none"
				class:active={system.id === selected}
				aria-current={system.id === selected ? 'true' : undefined}
				href="{resolve('/collections')}{systemsSearch({ system: system.id, gamePage: 1, find })}"
			>
				<span class="d-flex align-items-center gap-2 min-w-0">
					<GroupLogo
						kind="systems"
						item={{ key: system.id, name: system.displayName, logoUrl: system.logoUrl }}
					/>
					<span class="text-truncate" title={system.displayName}>{system.displayName}</span>
				</span>
				<span
					class="badge rounded-pill count-badge {system.id === selected
						? 'text-bg-light'
						: 'text-bg-primary'}"
					>{formatCount(system.gameCount)} {system.gameCount === 1 ? 'game' : 'games'}</span
				>
			</a>
			{#if auth.isAdmin && onedit}
				<button
					type="button"
					class="btn btn-link btn-sm border-start rounded-0 px-2"
					aria-label="Change logo for {system.displayName}"
					title="Change logo"
					onclick={() => onedit(system)}><Icon name="edit" /></button
				>
			{/if}
		</li>
	{/each}
</ul>
