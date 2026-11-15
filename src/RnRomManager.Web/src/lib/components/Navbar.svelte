<script lang="ts">
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import type { PageRouteId } from '$app/types';
	import type { Me } from '../api/types';
	import { formatCount } from '../format';
	import Icon from './Icon.svelte';
	import ThemeToggle from './ThemeToggle.svelte';

	interface Props {
		me: Me;
		/** Pending review items (Admin); null until loaded. */
		reviewCount: number | null;
		/** Deleted ROMs (Admin, D6); null until loaded. */
		deletedCount?: number | null;
		onlogout: () => void;
		/** Opens the global search; the button is hidden while the password must be changed. */
		onsearch?: () => void;
		/** Opens the keyboard shortcuts (B25); hidden while the password must be changed. */
		onhotkeys?: () => void;
		/** Bundle Creator B1: files in the working cart; the cart link shows when set and the password is changed. */
		cartCount?: number;
	}

	let {
		me,
		reviewCount,
		deletedCount = null,
		onlogout,
		onsearch,
		onhotkeys,
		cartCount
	}: Props = $props();

	// The link's name is its visible text plus hidden words ("Bundle 2 files in the cart"), so axe's
	// label-content-name-mismatch holds: visible text must be part of the accessible name.
	const cartFiles = $derived(cartCount === 1 ? 'file' : 'files');

	type NavLink = {
		href: Exclude<PageRouteId, `${string}[${string}`>;
		label: string;
		icon: string;
	};

	type NavItem = (NavLink | { label: string; icon: string; links: NavLink[] }) & {
		admin?: boolean;
	};

	const navigation: NavItem[] = [
		{ href: '/', label: 'Library', icon: 'library' },
		{ href: '/collections', label: 'Collections', icon: 'collections' },
		{ href: '/reports', label: 'Reports', icon: 'reports' },
		{
			label: 'ROM Tools',
			icon: 'tools',
			admin: true,
			links: [
				{ href: '/ingestion', label: 'Ingestion', icon: 'ingest' },
				{ href: '/review', label: 'Review', icon: 'review' },
				{ href: '/deleted', label: 'Deleted ROMs', icon: 'delete' },
				{ href: '/integrity', label: 'Integrity', icon: 'integrity' }
			]
		},
		{ href: '/admin/users', label: 'Admin', icon: 'admin', admin: true }
	];
	const visibleNavigation = $derived(
		navigation.filter((item) => !item.admin || me.roles.includes('admin'))
	);

	function isActive(href: string): boolean {
		const path = page.url.pathname;
		if (href === '/') return path === '/' || path.startsWith('/games/');
		return path === href || path.startsWith(href.split('/').slice(0, 2).join('/') + '/');
	}
</script>

<!-- B26: sticky; modals (1055) and dropdowns still render above it (sticky-top is 1020). -->
<nav class="navbar navbar-expand-lg bg-body-tertiary border-bottom sticky-top">
	<div class="container">
		<a
			class="navbar-brand d-flex align-items-center"
			href={resolve('/')}
			aria-label="RnRomManager home"
		>
			<Icon name="system-gb" />
		</a>
		<button
			class="navbar-toggler"
			type="button"
			data-bs-toggle="collapse"
			data-bs-target="#main-nav"
			aria-controls="main-nav"
			aria-expanded="false"
			aria-label="Toggle navigation"
		>
			<span class="navbar-toggler-icon"></span>
		</button>
		<div class="collapse navbar-collapse" id="main-nav">
			<ul class="navbar-nav me-auto">
				{#each visibleNavigation as item (item.label)}
					{#if 'links' in item}
						<li class="nav-item dropdown">
							<button
								type="button"
								class="btn btn-link nav-link dropdown-toggle d-flex align-items-center gap-1"
								class:active={item.links.some((link) => isActive(link.href))}
								data-bs-toggle="dropdown"
								aria-expanded="false"
							>
								<Icon name={item.icon} />
								{item.label}
								{#if item.links.some((link) => link.href === '/review') && reviewCount}
									<span class="badge rounded-pill text-bg-danger" aria-hidden="true">
										{formatCount(reviewCount)}
									</span>
									<span class="visually-hidden">{formatCount(reviewCount)} pending review</span>
								{/if}
							</button>
							<ul class="dropdown-menu navigation-menu">
								{#each item.links as link (link.href)}
									<li>{@render navigationLink(link, true)}</li>
								{/each}
							</ul>
						</li>
					{:else}
						<li class="nav-item">{@render navigationLink(item)}</li>
					{/if}
				{/each}
			</ul>
			<ul class="navbar-nav align-items-lg-center navigation-utilities">
				{#if cartCount !== undefined && !me.mustChangePassword}
					<li class="nav-item">
						<a
							class="nav-link d-flex align-items-center gap-1"
							class:active={isActive('/bundle-creator')}
							aria-current={isActive('/bundle-creator') ? 'page' : undefined}
							href={resolve('/bundle-creator')}
						>
							<!-- eslint-disable-next-line svelte/no-useless-mustaches -- Svelte trims a literal space at the start of a block -->
							<Icon name="cart" /> Bundle{#if cartCount}{' '}
								<span class="badge rounded-pill text-bg-primary">{formatCount(cartCount)}</span>
								<span class="visually-hidden">{cartFiles} in the cart</span>
							{/if}
						</a>
					</li>
				{/if}
				{#if onsearch && !me.mustChangePassword}
					<li class="nav-item">
						<button
							type="button"
							class="btn btn-link nav-link d-flex align-items-center gap-1"
							onclick={onsearch}
						>
							<Icon name="search" /> Search
						</button>
					</li>
				{/if}
				{#if onhotkeys && !me.mustChangePassword}
					<li class="nav-item">
						<button
							type="button"
							class="btn btn-link nav-link d-flex align-items-center"
							aria-label="Keyboard shortcuts"
							title="Keyboard shortcuts"
							onclick={onhotkeys}
						>
							<Icon name="help" />
						</button>
					</li>
				{/if}
				<li class="nav-item"><ThemeToggle /></li>
				<li class="nav-item dropdown">
					<button
						type="button"
						class="btn btn-link nav-link dropdown-toggle d-flex align-items-center gap-1"
						data-bs-toggle="dropdown"
						aria-expanded="false"
					>
						<Icon name="user" />
						{me.username}
					</button>
					<ul class="dropdown-menu dropdown-menu-end">
						<li>
							<a
								class="dropdown-item d-flex align-items-center gap-2"
								href={resolve('/change-password')}
							>
								<Icon name="password" /> Change password
							</a>
						</li>
						<li><hr class="dropdown-divider" /></li>
						<li>
							<button
								type="button"
								class="dropdown-item d-flex align-items-center gap-2"
								onclick={onlogout}
							>
								<Icon name="logout" /> Logout
							</button>
						</li>
					</ul>
				</li>
			</ul>
		</div>
	</div>
</nav>

{#snippet navigationLink(link: NavLink, inDropdown = false)}
	<a
		class={inDropdown
			? 'dropdown-item d-flex align-items-center gap-2'
			: 'nav-link d-flex align-items-center gap-1'}
		class:active={isActive(link.href)}
		aria-current={isActive(link.href) ? 'page' : undefined}
		href={resolve(link.href)}
	>
		<Icon name={link.icon} />
		{link.label}
		{#if link.href === '/review' && reviewCount}
			<span class="badge rounded-pill text-bg-danger">
				{formatCount(reviewCount)}<span class="visually-hidden">&#32;pending</span>
			</span>
		{/if}
		{#if link.href === '/deleted' && deletedCount}
			<span class="badge rounded-pill text-bg-secondary">
				{formatCount(deletedCount)}<span class="visually-hidden">&#32;deleted</span>
			</span>
		{/if}
	</a>
{/snippet}

<style>
	.navigation-menu {
		--bs-dropdown-link-active-bg: var(--bs-primary-bg-subtle);
		--bs-dropdown-link-active-color: var(--bs-primary-text-emphasis);
	}

	.navigation-menu .dropdown-item.active {
		font-weight: 600;
	}

	@media (min-width: 992px) {
		.navigation-menu {
			max-height: calc(100dvh - 4rem);
			overflow-y: auto;
		}
	}

	@media (max-width: 991.98px) {
		.navbar-collapse:global(.show) {
			max-height: calc(100dvh - 4rem);
			overflow-y: auto;
		}

		.navigation-menu {
			margin: 0.25rem 0.5rem 0.5rem;
		}

		.navigation-utilities {
			margin-top: 0.5rem;
			padding-top: 0.5rem;
			border-top: 1px solid var(--bs-border-color);
		}
	}
</style>
