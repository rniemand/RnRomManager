<script lang="ts">
	import { untrack } from 'svelte';
	import BaseModal from './BaseModal.svelte';
	import FormField from '#lib/components/forms/FormField.svelte';
	import TextInput from '#lib/components/forms/TextInput.svelte';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import { passwordError, policyHint } from '#lib/passwordPolicy.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import type { UserInfo } from '#lib/types/index.js';

	interface Props {
		/** Opening starts with an empty password. */
		open: boolean;
		user: UserInfo | null;
		/** A failure other than validation, after the modal closes. */
		onfailed: (e: unknown) => void;
	}

	let { open = $bindable(), user, onfailed }: Props = $props();

	let password = $state('');
	let error = $state<string | undefined>();
	let resetting = $state(false);

	$effect(() => {
		if (!open) return;
		untrack(() => {
			password = '';
			error = undefined;
		});
	});

	async function reset(): Promise<void> {
		if (!user) return;
		error = passwordError(password, user.username);
		if (error) return;
		resetting = true;
		try {
			await api.post(`/api/users/${user.id}/reset-password`, { newPassword: password });
			open = false;
			toasts.show(`Password reset. ${user.username} must change it at next sign-in.`, 'success');
		} catch (e) {
			if (isApiError(e, 'ValidationFailed')) error = e.errors.newPassword?.[0] ?? e.title;
			else {
				open = false;
				onfailed(e);
			}
		} finally {
			resetting = false;
		}
	}
</script>

<BaseModal
	bind:open
	title="Reset password"
	confirmLabel="Reset password"
	busy={resetting}
	onconfirm={reset}
>
	<p>
		Set a new password for {user?.username}. They must change it at next sign-in, and their lockout
		is cleared.
	</p>
	<FormField label="New password" for="reset-password">
		<TextInput
			id="reset-password"
			type="password"
			class={error ? 'is-invalid' : undefined}
			autocomplete="new-password"
			aria-describedby="reset-password-help"
			bind:value={password}
		/>
		{#if error}<div class="invalid-feedback">{error}</div>{/if}
		<div id="reset-password-help" class="form-text">{policyHint}</div>
	</FormField>
</BaseModal>
