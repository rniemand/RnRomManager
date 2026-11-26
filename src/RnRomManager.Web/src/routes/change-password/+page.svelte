<script lang="ts">
	import TextInput from '#lib/components/forms/TextInput.svelte';
	import Button from '#lib/components/forms/Button.svelte';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import { navigate } from '#lib/navigation.js';
	import { maxLength, policyHint, validateChangePassword } from '#lib/passwordPolicy.js';
	import type { ChangePasswordForm, FieldErrors } from '#lib/types/index.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';

	let form = $state<ChangePasswordForm>({
		currentPassword: '',
		newPassword: '',
		confirmPassword: ''
	});
	let errors = $state<FieldErrors>({});
	let failure = $state<unknown>(null);
	let busy = $state(false);

	const forced = $derived(auth.me?.mustChangePassword ?? false);

	async function submit(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		failure = null;
		errors = validateChangePassword(form, auth.me?.username ?? '');
		if (Object.keys(errors).length > 0) return;

		busy = true;
		try {
			await api.post('/api/auth/change-password', {
				currentPassword: form.currentPassword,
				newPassword: form.newPassword
			});
			await auth.load(); // clears mustChangePassword
			toasts.show('Password changed.', 'success');
			await navigate('/');
		} catch (e) {
			if (isApiError(e, 'ValidationFailed')) {
				errors = {
					currentPassword: e.errors.currentPassword?.[0],
					newPassword: e.errors.newPassword?.[0]
				};
			} else failure = e;
		} finally {
			busy = false;
		}
	}
</script>

<svelte:head><title>Change password · RnRomManager</title></svelte:head>

<div class="row justify-content-center">
	<div class="col-12 col-md-8 col-lg-5">
		<h1 class="h3 mb-3">Change password</h1>
		{#if forced}
			<div class="alert alert-info" role="status">Choose a new password before you continue.</div>
		{/if}
		{#if failure}
			<ErrorAlert error={failure} ondismiss={() => (failure = null)} />
		{/if}
		<form class="card card-body" onsubmit={submit} novalidate>
			<!-- Lets password managers pair the new password with the account. -->
			<TextInput
				class="d-none"
				autocomplete="username"
				value={auth.me?.username ?? ''}
				readonly
				tabindex={-1}
				aria-hidden="true"
			/>
			<div class="mb-3">
				<label class="form-label" for="current-password">Current password</label>
				<TextInput
					class={errors.currentPassword ? 'is-invalid' : undefined}
					id="current-password"
					type="password"
					autocomplete="current-password"
					aria-describedby="current-password-error"
					bind:value={form.currentPassword}
				/>
				<div id="current-password-error" class="invalid-feedback">{errors.currentPassword}</div>
			</div>
			<div class="mb-3">
				<label class="form-label" for="new-password">New password</label>
				<TextInput
					class={errors.newPassword ? 'is-invalid' : undefined}
					id="new-password"
					type="password"
					autocomplete="new-password"
					maxlength={maxLength}
					aria-describedby="new-password-hint new-password-error"
					bind:value={form.newPassword}
				/>
				<div id="new-password-hint" class="form-text">{policyHint}</div>
				<div id="new-password-error" class="invalid-feedback">{errors.newPassword}</div>
			</div>
			<div class="mb-3">
				<label class="form-label" for="confirm-password">Confirm new password</label>
				<TextInput
					class={errors.confirmPassword ? 'is-invalid' : undefined}
					id="confirm-password"
					type="password"
					autocomplete="new-password"
					maxlength={maxLength}
					aria-describedby="confirm-password-error"
					bind:value={form.confirmPassword}
				/>
				<div id="confirm-password-error" class="invalid-feedback">{errors.confirmPassword}</div>
			</div>
			<Button variant="primary" type="submit" disabled={busy}
				>{#if busy}<span class="spinner-border spinner-border-sm" aria-hidden="true"></span>{/if}
				Change password</Button
			>
		</form>
	</div>
</div>
