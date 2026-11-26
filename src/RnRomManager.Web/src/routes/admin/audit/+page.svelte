<script lang="ts">
	import Checkbox from '#lib/components/forms/Checkbox.svelte';
	import TextInput from '#lib/components/forms/TextInput.svelte';
	import SearchInput from '#lib/components/forms/SearchInput.svelte';
	import FormField from '#lib/components/forms/FormField.svelte';
	import Button from '#lib/components/forms/Button.svelte';
	import DataTable from '#lib/components/common/DataTable.svelte';
	import { useAbortableGet } from '#lib/loadPage.svelte.js';
	import { debounced } from '#lib/debounce.js';
	import { replaceUrl } from '#lib/urlState.js';
	import { onDestroy } from 'svelte';
	import { SvelteDate, SvelteURLSearchParams } from 'svelte/reactivity';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import type { Page, SystemInfo } from '#lib/types/index.js';
	import AdminOnly from '#lib/components/common/AdminOnly.svelte';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import FilterDropdown from '#lib/components/forms/FilterDropdown.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import Pagination from '#lib/components/common/Pagination.svelte';
	import SystemBadge from '#lib/components/common/SystemBadge.svelte';
	import { formatDateTime } from '#lib/format.js';
	import { pageCount } from '#lib/pageSize.js';
	import { auth } from '#lib/stores/auth.svelte.js';

	interface AuditItem {
		id: number;
		occurredAt: string;
		action: string;
		system: string | null;
		gameId: string | null;
		user: string | null;
		path: string | null;
		targetPath: string | null;
		details: string | null;
		/** B30: null when the game is gone. */
		gameTitle: string | null;
	}
	const keys = ['action', 'system', 'user', 'scheduled', 'q', 'from', 'to', 'page'] as const;
	type Key = (typeof keys)[number];
	const params = $derived(page.url.searchParams);
	const currentPage = $derived(Math.max(1, Number(params.get('page')) || 1));
	const scheduled = $derived(params.get('scheduled') === 'true');
	/** B25: what the User and Date toggles show beside their field name; null when unfiltered. */
	const userLabel = $derived(scheduled ? 'Scheduled' : params.get('user') || null);
	const dateLabel = $derived.by(() => {
		const from = params.get('from');
		const to = params.get('to');
		if (from && to) return `${from} – ${to}`;
		if (from) return `from ${from}`;
		return to ? `to ${to}` : null;
	});
	let actions = $state<string[]>([]);
	let systems = $state<SystemInfo[]>([]);
	let optionsError = $state<unknown>(null);
	let reload = $state(0);
	const setUser = debounced((value: string) => update({ user: value.trim() }), 300);
	const setSearch = debounced((value: string) => update({ q: value.trim() }), 300);
	function cancelTimers(): void {
		setUser.cancel();
		setSearch.cancel();
	}
	onDestroy(cancelTimers);

	function update(changes: Partial<Record<Key, string>>): void {
		const next = new SvelteURLSearchParams(params.toString());
		next.delete('page');
		for (const [key, value] of Object.entries(changes)) {
			if (value) next.set(key, value);
			else next.delete(key);
		}
		void replaceUrl(`/admin/audit${next.size ? `?${next}` : ''}`);
	}
	function clear(): void {
		cancelTimers();
		update(Object.fromEntries(keys.map((key) => [key, ''])));
	}
	function textInput(key: 'user' | 'q', value: string): void {
		if (key === 'user') setUser(value);
		else setSearch(value);
	}
	function setScheduled(checked: boolean): void {
		setUser.cancel();
		update({ scheduled: checked ? 'true' : '', user: '' });
	}
	function midnight(day: string, nextDay: boolean): string {
		const date = new SvelteDate(`${day}T00:00:00`);
		if (nextDay) date.setDate(date.getDate() + 1);
		return Number.isNaN(date.getTime()) ? day : date.toISOString();
	}
	const apiUrl = $derived.by(() => {
		const query = new SvelteURLSearchParams();
		for (const key of keys) {
			const value = params.get(key);
			if (value && key !== 'page' && !(key === 'user' && scheduled))
				query.set(key, key === 'from' || key === 'to' ? midnight(value, key === 'to') : value);
		}
		query.set('page', String(currentPage));
		query.set('pageSize', '100');
		return `/api/admin/audit?${query}`;
	});
	$effect(() => {
		if (!auth.isAdmin) return;
		void reload;
		const controller = new AbortController();
		optionsError = null;
		Promise.all([
			api.get<string[]>('/api/admin/audit/actions', { signal: controller.signal }),
			api.get<SystemInfo[]>('/api/systems', { signal: controller.signal })
		]).then(
			([a, s]) => {
				actions = a;
				systems = s;
			},
			(e) => {
				if (!controller.signal.aborted) optionsError = e;
			}
		);
		return () => controller.abort();
	});
	const report = useAbortableGet<Page<AuditItem>>(() => {
		void reload;
		return auth.isAdmin ? apiUrl : null;
	});
	const result = $derived(report.result);
	const error = $derived(report.error);
</script>

<svelte:head><title>Auditing · RnRomManager</title></svelte:head>

<AdminOnly>
	<h2 class="h4">Auditing</h2>
	<!-- B25: one toolbar row like the Library's; each toggle names its field and chosen value; it wraps on narrow screens. -->
	<form
		class="d-flex flex-wrap align-items-center gap-2 mb-3"
		role="search"
		onsubmit={(e) => e.preventDefault()}
	>
		<FormField label="Search" for="audit-search" hiddenLabel>
			<SearchInput
				id="audit-search"
				size="sm"
				class="flex-grow-1 audit-search"
				placeholder="Search details and paths"
				maxlength={200}
				value={params.get('q') ?? ''}
				oninput={(e) => textInput('q', e.currentTarget.value)}
			/>
		</FormField>
		<FilterDropdown
			label="Action"
			options={actions.map((a) => ({ value: a, label: a }))}
			value={params.get('action')}
			all={{ label: 'All actions', sum: false }}
			filterPlaceholder="Filter actions…"
			onselect={(action) => update({ action: action ?? '' })}
		/>
		<FilterDropdown
			label="System"
			options={systems.map((s) => ({
				value: s.id,
				label: s.displayName,
				badge: { system: s.id, name: s.displayName },
				search: [s.shortName]
			}))}
			value={params.get('system')}
			all={{ label: 'All systems', sum: false }}
			filterPlaceholder="Filter systems…"
			onselect={(system) => update({ system: system ?? '' })}
		/>
		<div class="dropdown">
			<Button
				class="btn btn-sm dropdown-toggle {userLabel ? 'btn-secondary' : 'btn-outline-secondary'}"
				data-bs-toggle="dropdown"
				data-bs-auto-close="outside"
				aria-expanded="false"
			>
				User{#if userLabel}: {userLabel}{/if}
			</Button>
			<div class="dropdown-menu p-2 filter-panel">
				<FormField label="User" for="audit-user" labelClass="small mb-1">
					<TextInput
						id="audit-user"
						size="sm"
						value={scheduled ? '' : (params.get('user') ?? '')}
						disabled={scheduled}
						oninput={(e) => textInput('user', e.currentTarget.value)}
					/>
				</FormField>
				<Checkbox
					id="audit-scheduled"
					label="Scheduled only"
					wrapperClass="mt-2 mb-0"
					checked={scheduled}
					onchange={(e) => setScheduled(e.currentTarget.checked)}
				/>
			</div>
		</div>
		<div class="dropdown">
			<Button
				class="btn btn-sm dropdown-toggle {dateLabel ? 'btn-secondary' : 'btn-outline-secondary'}"
				data-bs-toggle="dropdown"
				data-bs-auto-close="outside"
				aria-expanded="false"
			>
				Date{#if dateLabel}: {dateLabel}{/if}
			</Button>
			<div class="dropdown-menu p-2 filter-panel">
				<FormField label="From" for="audit-from" labelClass="small mb-1">
					<TextInput
						id="audit-from"
						type="date"
						size="sm"
						class="mb-2"
						value={params.get('from') ?? ''}
						onchange={(e) => update({ from: e.currentTarget.value })}
					/>
				</FormField>
				<FormField label="To" for="audit-to" labelClass="small mb-1">
					<TextInput
						id="audit-to"
						type="date"
						size="sm"
						value={params.get('to') ?? ''}
						onchange={(e) => update({ to: e.currentTarget.value })}
					/>
				</FormField>
			</div>
		</div>
		<Button
			variant="secondary"
			outline
			size="sm"
			disabled={!keys.some((key) => params.has(key))}
			onclick={clear}>Clear</Button
		>
	</form>
	{#if optionsError}<ErrorAlert error={optionsError} onretry={() => reload++} />{/if}
	<!-- B30: pagination above and below the table, with distinct landmark labels. -->
	<div class="mb-3">{@render pager('Pages, top')}</div>
	{#if error}
		<ErrorAlert {error} onretry={() => reload++} />
	{:else if !result}
		<Loading />
	{:else if result.items.length === 0}
		<div class="card"><EmptyState message="No audit events found." /></div>
	{:else}
		<!-- Release editing B35: below md, one card per event instead of the table. -->
		<div class="d-md-none">
			{#each result.items as item (item.id)}
				<div class="card mb-2">
					<div class="card-body py-2">
						<div class="d-flex align-items-center gap-2">
							<strong>{item.action}</strong>
							{#if item.system}<SystemBadge
									system={item.system}
									name={systems.find((s) => s.id === item.system)?.displayName}
								/>{/if}
							<span class="ms-auto small text-body-secondary text-nowrap"
								>{formatDateTime(item.occurredAt)}</span
							>
						</div>
						<div>{@render who(item)} · {@render gameCell(item)}</div>
						<div class="small text-break">{@render changeText(item)}</div>
					</div>
				</div>
			{/each}
		</div>
		<!-- B30: five wrapping columns, no sideways scroll at ≥ 992 px (the wrapper stays for rule T1). -->
		<DataTable items={result.items} key={(item) => item.id} class="d-none d-md-block">
			{#snippet head()}
				<tr>
					<th scope="col">Time</th>
					<th scope="col">Action</th>
					<th scope="col">User</th>
					<th scope="col">Game</th>
					<th scope="col">Change</th>
				</tr>
			{/snippet}
			{#snippet row(item)}<tr>
					<td class="text-nowrap">{formatDateTime(item.occurredAt)}</td>
					<td
						><div>{item.action}</div>
						{#if item.system}<SystemBadge
								system={item.system}
								name={systems.find((s) => s.id === item.system)?.displayName}
							/>{/if}</td
					>
					<td>{@render who(item)}</td>
					<td class="text-break">{@render gameCell(item)}</td>
					<td class="text-break small">{@render changeText(item)}</td>
				</tr>{/snippet}
		</DataTable>
	{/if}
	{@render pager('Pages, bottom')}
</AdminOnly>

<!-- Release editing B35: the cells the table and the phone cards share. -->
{#snippet who(item: AuditItem)}{#if item.user}{item.user}{:else}<em>System</em>{/if}{/snippet}

{#snippet gameCell(item: AuditItem)}{#if item.gameId && item.gameTitle}<a
			href={resolve('/games/[id]', { id: item.gameId })}>{item.gameTitle}</a
		>{:else if item.gameId}<span title={item.gameId}>{item.gameId.slice(0, 8)}</span
		>{:else}—{/if}{/snippet}

{#snippet changeText(
	item: AuditItem
)}{#if item.path || item.targetPath || item.details}{#if item.path}<div>
				{item.path}{#if item.targetPath}{` → ${item.targetPath}`}{/if}
			</div>{:else if item.targetPath}<div>
				→ {item.targetPath}
			</div>{/if}{#if item.details}<div class="text-body-secondary">
				{item.details}
			</div>{/if}{:else}—{/if}{/snippet}

{#snippet pager(label: string)}
	{#if result && result.total > 0}<Pagination
			page={currentPage}
			pages={pageCount(result.total, 100)}
			onpage={(p) => update({ page: String(p) })}
			{label}
		/>{/if}
{/snippet}

<style>
	/* On the SearchInput's input, which this page doesn't render itself. */
	:global(.audit-search) {
		min-width: 12rem;
		max-width: 24rem;
	}
	.filter-panel {
		min-width: 14rem;
	}
	/* Release editing B35: below md the search takes the toolbar's first row. */
	@media (max-width: 767.98px) {
		:global(.audit-search) {
			flex-basis: 100%;
			max-width: none;
		}
	}
</style>
