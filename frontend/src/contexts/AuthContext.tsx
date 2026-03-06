import { createContext, useContext, useState, useEffect } from 'react';
import type { ReactNode } from 'react';
import { apiClient } from '../api/client';
import type { User } from '../types';

interface AuthContextValue {
  user: User | null;
  isLoading: boolean;
  isAuthenticated: boolean;
  login: (token: string, acumaticaToken?: string) => Promise<void>;
  logout: (reason?: string) => void;
  isManagerOrAbove: boolean;
  isAdmin: boolean;
}

const AuthContext = createContext<AuthContextValue | null>(null);

// Persisted in localStorage so explicit sign-out survives browser restarts.
const LOGGED_OUT_KEY = 'auth-logged-out';

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  useEffect(() => {
    // If the user explicitly signed out, show the login page (persists across restarts)
    if (localStorage.getItem(LOGGED_OUT_KEY)) {
      setIsLoading(false);
      return;
    }

    const token = sessionStorage.getItem('access_token');
    if (token) {
      // Validate the stored token; if invalid, clear it and show login page.
      fetchCurrentUser().catch(() => {
        sessionStorage.removeItem('access_token');
        setIsLoading(false);
      });
    } else {
      // No token — show the login page. User must sign in explicitly.
      setIsLoading(false);
    }
  }, []);

  // Refresh user info when the tab regains focus so role/permission changes
  // made by an admin take effect without requiring a full re-login.
  useEffect(() => {
    const onFocus = () => {
      if (sessionStorage.getItem('access_token')) {
        fetchCurrentUser().catch(() => {});
      }
    };
    window.addEventListener('focus', onFocus);
    return () => window.removeEventListener('focus', onFocus);
  }, []);

  const fetchCurrentUser = async () => {
    try {
      const { data } = await apiClient.get('/auth/me');
      setUser(data);
    } catch (err) {
      sessionStorage.removeItem('access_token');
      throw err;
    } finally {
      setIsLoading(false);
    }
  };

  const login = async (token: string, acumaticaToken?: string) => {
    localStorage.removeItem(LOGGED_OUT_KEY);
    sessionStorage.setItem('access_token', token);
    if (acumaticaToken) sessionStorage.setItem('acumatica_token', acumaticaToken);
    setIsLoading(true);
    await fetchCurrentUser();
  };

  const logout = (reason?: string) => {
    sessionStorage.removeItem('access_token');
    sessionStorage.removeItem('acumatica_token');
    localStorage.setItem(LOGGED_OUT_KEY, '1');
    setUser(null);
    window.location.href = reason ? `/login?error=${reason}` : '/login';
  };

  return (
    <AuthContext.Provider value={{
      user,
      isLoading,
      isAuthenticated: !!user,
      login,
      logout,
      isManagerOrAbove: user?.role === 'Manager' || user?.role === 'Admin',
      isAdmin: user?.role === 'Admin',
    }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider');
  return ctx;
}
