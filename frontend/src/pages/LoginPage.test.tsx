import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { LoginPage } from './LoginPage';
import { AnonWrapper, makeAuthValue } from '../test-utils';
import { AuthContext } from '../auth/AuthContext';
import { I18nProvider } from '../i18n';

afterEach(() => {
  cleanup();
});

function renderLogin(login: (email: string, password: string) => Promise<void>) {
  const value = makeAuthValue({ status: 'anon' }, { login });
  return render(
    <MemoryRouter>
      <I18nProvider>
        <AuthContext.Provider value={value}>
          <LoginPage />
        </AuthContext.Provider>
      </I18nProvider>
    </MemoryRouter>,
  );
}

describe('LoginPage', () => {
  it('renders email + password fields and a submit button', () => {
    render(
      <MemoryRouter>
        <AnonWrapper>
          <LoginPage />
        </AnonWrapper>
      </MemoryRouter>,
    );

    expect(screen.getByLabelText('Email')).toBeInTheDocument();
    expect(screen.getByLabelText('Password')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Accedi' })).toBeInTheDocument();
  });

  it('calls login() with the typed credentials on submit', async () => {
    const loginSpy = vi.fn(async (_email: string, _password: string) => {
      return undefined;
    });
    renderLogin(loginSpy);

    const user = userEvent.setup();
    await user.type(screen.getByLabelText('Email'), 'dev@nubarca.local');
    await user.type(screen.getByLabelText('Password'), 'hunter2');
    await user.click(screen.getByRole('button', { name: 'Accedi' }));

    await waitFor(() => {
      expect(loginSpy).toHaveBeenCalledWith('dev@nubarca.local', 'hunter2');
    });
  });

  it('shows the friendly error message when login rejects', async () => {
    const loginSpy = vi.fn(async () => {
      throw new Error('Invalid email or password.');
    });
    renderLogin(loginSpy);

    const user = userEvent.setup();
    await user.type(screen.getByLabelText('Email'), 'dev@nubarca.local');
    await user.type(screen.getByLabelText('Password'), 'wrong');
    await user.click(screen.getByRole('button', { name: 'Accedi' }));

    expect(
      await screen.findByText('Invalid email or password.'),
    ).toBeInTheDocument();
  });
});
