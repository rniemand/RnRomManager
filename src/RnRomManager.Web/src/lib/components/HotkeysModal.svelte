<script lang="ts">
	import { tick, untrack } from 'svelte';
	import { isApiError } from '../api/errors';
	import type { Me } from '../api/types';
	import {
		actions,
		allowedFor,
		defaultBindings,
		errors as hotkeyErrors,
		fromEvent,
		ruleError,
		validate,
		type Bindings,
		type EditableHotkeyId,
		type HotkeyAction
	} from '../hotkeys';
	import { hotkeys } from '../stores/hotkeys.svelte';
	import { toasts } from '../stores/toasts.svelte';
	import ConfirmModal from './ConfirmModal.svelte';
	import ErrorAlert from './ErrorAlert.svelte';

	/** B25: the user's global hotkeys; edits are pending until Save. */
	interface Props {
		open: boolean;
		me: Me;
	}

	let { open = $bindable(), me }: Props = $props();

	let pending = $state<Bindings | null>(null);
	let capturing = $state<EditableHotkeyId | null>(null);
	let captureError = $state<{ id: EditableHotkeyId; message: string } | null>(null);
	let serverErrors = $state<Partial<Record<string, string>>>({});
	let saveError = $state<unknown>(null);
	let busy = $state(false);

	const rows = $derived(actions.filter((a) => allowedFor(a, me.roles)));
	const ruleErrors = $derived(pending ? validate(pending, me.roles) : {});
	const changed = $derived(
		!!pending && !!hotkeys.bindings && JSON.stringify(pending) !== JSON.stringify(hotkeys.bindings)
	);
	const blocked = $derived(
		!pending || !changed || Object.keys(ruleErrors).length > 0 || capturing !== null
	);

	$effect(() => {
		if (!open) return;
		const current = untrack(() => hotkeys.bindings);
		pending = current ? { ...current } : null;
		capturing = null;
		captureError = null;
		serverErrors = {};
		saveError = null;
		busy = false;
	});

	// Opened before the bindings arrived: pick them up when they do.
	$effect(() => {
		if (open && !pending && hotkeys.bindings) pending = { ...hotkeys.bindings };
	});

	const modifierCodes = /^(Shift|Control|Alt|Meta|OS)(Left|Right)?$/;

	function edit(id: EditableHotkeyId): void {
		captureError = null;
		capturing = capturing === id ? null : id;
		// Safari doesn't focus a clicked button: capture listens on it, so focus it.
		if (capturing) void tick().then(() => document.getElementById(`hotkey-edit-${id}`)?.focus());
	}

	/** Capture mode: the next non-modifier keydown becomes the binding (B25). */
	function capture(e: KeyboardEvent, id: EditableHotkeyId): void {
		if (capturing !== id || !pending) return;
		// Keep it from Bootstrap (Escape closes the modal), global search and the global hotkeys.
		e.preventDefault();
		e.stopPropagation();
		const plain = !e.ctrlKey && !e.altKey && !e.shiftKey && !e.metaKey;
		if (plain && e.key === 'Escape') {
			capturing = null;
			captureError = null;
			return;
		}
		if (plain && (e.key === 'Backspace' || e.key === 'Delete')) {
			pending[id] = null;
			delete serverErrors[`bindings.${id}`];
			capturing = null;
			captureError = null;
			return;
		}
		if (modifierCodes.test(e.code)) return;
		const combination = fromEvent(e);
		const other = rows.find(
			(a): a is HotkeyAction & { id: EditableHotkeyId } =>
				a.editable && a.id !== id && pending![a.id as EditableHotkeyId] === combination
		);
		const message =
			combination === null
				? hotkeyErrors.format
				: (ruleError(combination) ?? (other ? hotkeyErrors.duplicate(other.name) : null));
		if (message) {
			captureError = { id, message };
			return;
		}
		pending[id] = combination;
		delete serverErrors[`bindings.${id}`];
		capturing = null;
		captureError = null;
	}

	/**
	 * A listener on the button itself, not Svelte's delegated `onkeydown` (which runs at the root, after Bootstrap's
	 * Escape handler on `.modal` has already closed the modal).
	 */
	function captureKeys(id: EditableHotkeyId) {
		return (node: HTMLElement) => {
			const listener = (e: KeyboardEvent) => capture(e, id);
			node.addEventListener('keydown', listener);
			return () => node.removeEventListener('keydown', listener);
		};
	}

	function reset(): void {
		if (!pending) return;
		const defaults = defaultBindings();
		for (const row of rows)
			if (row.editable) pending[row.id as EditableHotkeyId] = defaults[row.id as EditableHotkeyId];
		capturing = null;
		captureError = null;
		serverErrors = {};
	}

	async function save(): Promise<void> {
		if (!pending || blocked || busy) return;
		busy = true;
		saveError = null;
		try {
			await hotkeys.save(pending);
			toasts.show('Keyboard shortcuts saved.', 'success');
			open = false;
		} catch (e) {
			if (isApiError(e, 'ValidationFailed')) {
				serverErrors = Object.fromEntries(Object.entries(e.errors).map(([k, v]) => [k, v[0]]));
			}
			saveError = e;
		} finally {
			busy = false;
		}
	}

	const keys = (binding: string) => binding.split('+');
	const errorFor = (id: string) =>
		(captureError?.id === id ? captureError.message : null) ??
		ruleErrors[id as EditableHotkeyId] ??
		serverErrors[`bindings.${id}`] ??
		null;
</script>

<ConfirmModal
	bind:open
	title="Keyboard shortcuts"
	size="lg"
	confirmLabel="Save"
	{busy}
	confirmDisabled={blocked}
	onconfirm={save}
>
	{#if saveError}<ErrorAlert error={saveError} ondismiss={() => (saveError = null)} />{/if}
	{#if !pending}
		<p class="text-body-secondary">Your shortcuts couldn't be loaded.</p>
		<button type="button" class="btn btn-sm btn-outline-secondary" onclick={() => hotkeys.load()}
			>Retry</button
		>
	{:else}
		<table class="table table-sm table-striped table-hover align-middle mb-3">
			<caption class="visually-hidden">Keyboard shortcuts</caption>
			<thead class="table-dark">
				<tr class="small text-uppercase text-body-secondary">
					<th scope="col">Action</th>
					<th scope="col">Shortcut</th>
					<th scope="col"><span class="visually-hidden">Edit</span></th>
				</tr>
			</thead>
			<tbody>
				{#each rows as action (action.id)}
					{@const id = action.id as EditableHotkeyId}
					{@const error = action.editable ? errorFor(id) : null}
					<tr data-action={action.id}>
						<th scope="row" class="fw-normal">{action.name}</th>
						<td>
							{#if !action.editable}
								{#each action.defaults as binding, i (binding)}
									{#if i > 0}<span class="text-body-secondary small"> or </span>{/if}
									{#each keys(binding) as key, k (k)}{#if k > 0}+{/if}<kbd>{key}</kbd>{/each}
								{/each}
							{:else if capturing === id}
								<span class="text-primary" aria-live="polite">Press keys…</span>
							{:else if pending[id]}
								{#each keys(pending[id]!) as key, k (k)}{#if k > 0}+{/if}<kbd>{key}</kbd>{/each}
							{:else}
								<span class="text-body-secondary">Not assigned</span>
							{/if}
							{#if error}<div class="small text-danger" role="alert">{error}</div>{/if}
						</td>
						<td class="text-end">
							{#if action.editable}
								<button
									type="button"
									id="hotkey-edit-{id}"
									class="btn btn-sm"
									class:btn-outline-secondary={capturing !== id}
									class:btn-primary={capturing === id}
									aria-label={capturing === id
										? `Cancel editing ${action.name}`
										: `Edit ${action.name}`}
									aria-pressed={capturing === id}
									onclick={() => edit(id)}
									{@attach captureKeys(id)}
								>
									{capturing === id ? 'Cancel' : 'Edit'}
								</button>
							{/if}
						</td>
					</tr>
				{/each}
			</tbody>
		</table>
		<p class="small text-body-secondary">
			Edit, then press the new keys. Backspace or Delete clears a shortcut; Escape stops editing.
		</p>
		<button type="button" class="btn btn-sm btn-outline-secondary" onclick={reset}
			>Reset to defaults</button
		>
	{/if}
</ConfirmModal>
